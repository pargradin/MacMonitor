using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MacMonitor.Storage;

/// <summary>
/// SQLite-backed <see cref="IKnownGoodRepository"/>.
/// </summary>
public sealed class SqliteKnownGoodRepository : IKnownGoodRepository
{
    private const int InClauseBatchSize = 500;

    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<SqliteKnownGoodRepository> _logger;

    public SqliteKnownGoodRepository(
        SqliteConnectionFactory factory,
        IOptions<StorageOptions> options,
        ILogger<SqliteKnownGoodRepository> logger)
    {
        _factory = factory;
        _logger = logger;
        _ = options;
    }

    public async Task<bool> IsKnownGoodAsync(string toolName, string identityKey, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var result = await conn.ExecuteScalarAsync("""
            SELECT 1 FROM known_good WHERE tool_name = $tool AND identity_key = $key LIMIT 1;
            """,
            null, ct,
            ("$tool", toolName),
            ("$key", identityKey)).ConfigureAwait(false);
        return result is not null;
    }

    public async Task<IReadOnlySet<string>> FilterAllowedAsync(
        string toolName,
        IReadOnlyCollection<string> identityKeys,
        CancellationToken ct)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (identityKeys.Count == 0)
        {
            return allowed;
        }

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        // Batch the IN-clause; SQLite has a default parameter limit of 999.
        foreach (var batch in identityKeys.Chunk(InClauseBatchSize))
        {
            await using var cmd = conn.CreateCommand();
            var inList = string.Join(",", batch.Select((_, i) => $"$k{i}"));
            cmd.CommandText = $"""
                SELECT identity_key FROM known_good
                WHERE tool_name = $tool AND identity_key IN ({inList});
                """;
            cmd.Parameters.AddWithValue("$tool", toolName);
            for (var i = 0; i < batch.Length; i++)
            {
                cmd.Parameters.AddWithValue($"$k{i}", batch[i]);
            }
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                allowed.Add(reader.GetString(0));
            }
        }
        return allowed;
    }

    public async Task AddAsync(KnownGoodEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("""
            INSERT OR REPLACE INTO known_good (tool_name, identity_key, note, added_at)
            VALUES ($tool, $key, $note, $ts);
            """,
            null, ct,
            ("$tool", entry.ToolName),
            ("$key", entry.IdentityKey),
            ("$note", (object?)entry.Note),
            ("$ts", entry.AddedAt.ToUnixTimeSeconds())).ConfigureAwait(false);
        _logger.LogInformation("Marked known-good: {Tool} / {Key}.", entry.ToolName, entry.IdentityKey);
    }

    public async Task<bool> RemoveAsync(string toolName, string identityKey, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.ExecuteAsync("""
            DELETE FROM known_good WHERE tool_name = $tool AND identity_key = $key;
            """,
            null, ct,
            ("$tool", toolName),
            ("$key", identityKey)).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<IReadOnlyList<KnownGoodEntry>> ListAsync(string? toolName, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        if (toolName is null)
        {
            cmd.CommandText = """
                SELECT tool_name, identity_key, note, added_at
                FROM known_good
                ORDER BY added_at DESC;
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT tool_name, identity_key, note, added_at
                FROM known_good
                WHERE tool_name = $tool
                ORDER BY added_at DESC;
                """;
            cmd.Parameters.AddWithValue("$tool", toolName);
        }

        var rows = new List<KnownGoodEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new KnownGoodEntry(
                ToolName: reader.GetString(0),
                IdentityKey: reader.GetString(1),
                Note: reader.IsDBNull(2) ? null : reader.GetString(2),
                AddedAt: SqliteHelpers.FromUnixSeconds(reader.GetInt64(3))));
        }
        return rows;
    }
}
