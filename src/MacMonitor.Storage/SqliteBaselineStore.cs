using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MacMonitor.Storage;

/// <summary>
/// SQLite-backed <see cref="IBaselineStore"/>. Owns DB initialisation: creates the file,
/// turns on WAL, runs the migration runner. Snapshots are stored as a single JSON blob per
/// (scan, tool); diffs happen in C# via <see cref="DifferBase{T}"/>.
/// </summary>
public sealed class SqliteBaselineStore : IBaselineStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly StorageOptions _options;
    private readonly ILogger<SqliteBaselineStore> _logger;

    public SqliteBaselineStore(
        SqliteConnectionFactory factory,
        IOptions<StorageOptions> options,
        ILogger<SqliteBaselineStore> logger)
    {
        _factory = factory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var path = _factory.ResolvedPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        // Recommended for a single-writer worker: WAL gives us non-blocking readers
        // (the future Blazor UI / CLI list-findings) without contending with the writer.
        await conn.ExecuteAsync("PRAGMA journal_mode = WAL;", null, ct).ConfigureAwait(false);
        await conn.ExecuteAsync("PRAGMA synchronous = NORMAL;", null, ct).ConfigureAwait(false);
        await conn.ExecuteAsync("PRAGMA foreign_keys = ON;", null, ct).ConfigureAwait(false);

        // Bootstrap (creates schema_migrations if missing).
        await conn.ExecuteAsync(Schema.Bootstrap, null, ct).ConfigureAwait(false);

        // Apply pending migrations.
        var applied = await GetAppliedMigrationsAsync(conn, ct).ConfigureAwait(false);
        foreach (var (id, sql) in Schema.All)
        {
            if (applied.Contains(id))
            {
                continue;
            }
            _logger.LogInformation("Applying migration {Id}.", id);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await conn.ExecuteAsync(sql, tx, ct).ConfigureAwait(false);
            await conn.ExecuteAsync(
                $"INSERT INTO {Schema.MigrationsTableName} (id, applied_at) VALUES ($id, $ts);",
                tx, ct,
                ("$id", id),
                ("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds())).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Baseline DB ready at {Path}. Migrations applied: {Applied}.", path, Schema.All.Count);
    }

    public async Task<Snapshot?> GetLatestSnapshotAsync(string toolName, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand("""
            SELECT scan_id, captured_at, payload_json, payload_hash, item_count
            FROM snapshots
            WHERE tool_name = $tool
            ORDER BY captured_at DESC
            LIMIT 1;
            """,
            null,
            ("$tool", toolName));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return new Snapshot(
            ScanId: reader.GetString(0),
            ToolName: toolName,
            CapturedAt: SqliteHelpers.FromUnixSeconds(reader.GetInt64(1)),
            PayloadJson: reader.GetString(2),
            PayloadHash: reader.GetString(3),
            ItemCount: reader.GetInt32(4));
    }

    public async Task SaveSnapshotAsync(Snapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("""
            INSERT OR REPLACE INTO snapshots
                (scan_id, tool_name, captured_at, payload_json, payload_hash, item_count)
            VALUES ($scan, $tool, $ts, $json, $hash, $count);
            """,
            null, ct,
            ("$scan", snapshot.ScanId),
            ("$tool", snapshot.ToolName),
            ("$ts", snapshot.CapturedAt.ToUnixTimeSeconds()),
            ("$json", snapshot.PayloadJson),
            ("$hash", snapshot.PayloadHash),
            ("$count", snapshot.ItemCount)).ConfigureAwait(false);
    }

    public async Task ApplyRetentionAsync(CancellationToken ct)
    {
        var keep = Math.Max(1, _options.RetentionSnapshotsPerTool);
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        // Delete all but the latest <keep> snapshots per tool.
        var deleted = await conn.ExecuteAsync("""
            WITH ranked AS (
                SELECT scan_id, tool_name,
                       ROW_NUMBER() OVER (PARTITION BY tool_name ORDER BY captured_at DESC) AS rn
                FROM snapshots
            )
            DELETE FROM snapshots
            WHERE (scan_id, tool_name) IN (
                SELECT scan_id, tool_name FROM ranked WHERE rn > $keep
            );
            """,
            null, ct,
            ("$keep", keep)).ConfigureAwait(false);

        if (deleted > 0)
        {
            _logger.LogDebug("Retention dropped {N} old snapshot rows.", deleted);
            await conn.ExecuteAsync("PRAGMA wal_checkpoint(PASSIVE);", null, ct).ConfigureAwait(false);
        }
    }

    private static async Task<HashSet<string>> GetAppliedMigrationsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand($"SELECT id FROM {Schema.MigrationsTableName};", null);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            applied.Add(reader.GetString(0));
        }
        return applied;
    }
}
