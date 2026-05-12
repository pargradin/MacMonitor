using System.Text.Json;
using System.Text.Json.Serialization;
using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MacMonitor.Storage;

/// <summary>
/// SQLite-backed <see cref="IScanRepository"/>. Owns the <c>scans</c> and <c>findings</c> tables.
/// </summary>
public sealed class SqliteScanRepository : IScanRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<SqliteScanRepository> _logger;

    public SqliteScanRepository(
        SqliteConnectionFactory factory,
        IOptions<StorageOptions> options,
        ILogger<SqliteScanRepository> logger)
    {
        _factory = factory;
        _logger = logger;
        _ = options;
    }

    public async Task RecordScanStartedAsync(string scanId, DateTimeOffset startedAt, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("""
            INSERT OR REPLACE INTO scans (id, started_at, status)
            VALUES ($id, $ts, 'running');
            """,
            null, ct,
            ("$id", scanId),
            ("$ts", startedAt.ToUnixTimeSeconds())).ConfigureAwait(false);
    }

    public async Task RecordScanCompletedAsync(string scanId, DateTimeOffset completedAt, string status, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("""
            UPDATE scans SET completed_at = $ts, status = $status WHERE id = $id;
            """,
            null, ct,
            ("$ts", completedAt.ToUnixTimeSeconds()),
            ("$status", status),
            ("$id", scanId)).ConfigureAwait(false);
    }

    public async Task PersistFindingsAsync(IReadOnlyList<Finding> findings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (findings.Count == 0)
        {
            return;
        }

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO findings
                (id, scan_id, created_at, severity, category, source, summary, evidence_json,
                 rationale, recommended_action)
            VALUES ($id, $scan, $ts, $sev, $cat, $src, $sum, $evi, $rat, $act);
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pScan = cmd.Parameters.Add("$scan", SqliteType.Text);
        var pTs = cmd.Parameters.Add("$ts", SqliteType.Integer);
        var pSev = cmd.Parameters.Add("$sev", SqliteType.Text);
        var pCat = cmd.Parameters.Add("$cat", SqliteType.Text);
        var pSrc = cmd.Parameters.Add("$src", SqliteType.Text);
        var pSum = cmd.Parameters.Add("$sum", SqliteType.Text);
        var pEvi = cmd.Parameters.Add("$evi", SqliteType.Text);
        var pRat = cmd.Parameters.Add("$rat", SqliteType.Text);
        var pAct = cmd.Parameters.Add("$act", SqliteType.Text);

        foreach (var f in findings)
        {
            pId.Value = f.Id;
            pScan.Value = f.ScanId;
            pTs.Value = f.CreatedAt.ToUnixTimeSeconds();
            pSev.Value = f.Severity.ToString();
            pCat.Value = f.Category.ToString();
            pSrc.Value = f.Source;
            pSum.Value = f.Summary;
            pEvi.Value = f.Evidence is null
                ? DBNull.Value
                : JsonSerializer.Serialize(f.Evidence, f.Evidence.GetType(), JsonOptions);
            pRat.Value = (object?)f.Rationale ?? DBNull.Value;
            pAct.Value = (object?)f.RecommendedAction ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        _logger.LogDebug("Persisted {N} findings.", findings.Count);
    }

    public async Task<IReadOnlyList<Finding>> GetFindingsAsync(int limit, Severity? minSeverity, CancellationToken ct)
    {
        if (limit <= 0)
        {
            limit = 100;
        }

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var allowedSeverities = AllowedSeverityNames(minSeverity);
        // Build an IN-clause with positional parameters so we can pass any number safely.
        var sql = """
            SELECT id, scan_id, created_at, severity, category, source, summary, evidence_json,
                   rationale, recommended_action
            FROM findings
            WHERE severity IN (
            """ + string.Join(",", allowedSeverities.Select((_, i) => $"$s{i}")) + """
            )
            ORDER BY created_at DESC
            LIMIT $limit;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < allowedSeverities.Count; i++)
        {
            cmd.Parameters.AddWithValue($"$s{i}", allowedSeverities[i]);
        }
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<Finding>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            object? evidence = null;
            if (!reader.IsDBNull(7))
            {
                var json = reader.GetString(7);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    // Round-trip as JsonElement so callers can either re-serialize or inspect.
                    evidence = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
                }
            }
            rows.Add(new Finding(
                Id: reader.GetString(0),
                ScanId: reader.GetString(1),
                CreatedAt: SqliteHelpers.FromUnixSeconds(reader.GetInt64(2)),
                Severity: Enum.Parse<Severity>(reader.GetString(3), ignoreCase: true),
                Category: Enum.Parse<FindingCategory>(reader.GetString(4), ignoreCase: true),
                Source: reader.GetString(5),
                Summary: reader.GetString(6),
                Evidence: evidence,
                Rationale: reader.IsDBNull(8) ? null : reader.GetString(8),
                RecommendedAction: reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return rows;
    }

    public async Task<FindingsPage> QueryFindingsAsync(FindingsFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var limit = filter.Limit <= 0 ? 50 : filter.Limit;
        var offset = filter.Offset < 0 ? 0 : filter.Offset;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        // Build a WHERE clause incrementally so we can omit unused conditions.
        var allowedSeverities = AllowedSeverityNames(filter.MinSeverity);
        var where = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        where.Add("severity IN (" + string.Join(",", allowedSeverities.Select((_, i) => $"$s{i}")) + ")");
        for (var i = 0; i < allowedSeverities.Count; i++)
        {
            parameters.Add(($"$s{i}", allowedSeverities[i]));
        }
        if (!string.IsNullOrWhiteSpace(filter.Source))
        {
            where.Add("source = $src");
            parameters.Add(("$src", filter.Source));
        }
        if (filter.SinceUtc is { } since)
        {
            where.Add("created_at >= $since");
            parameters.Add(("$since", since.ToUnixTimeSeconds()));
        }
        if (filter.UntilUtc is { } until)
        {
            where.Add("created_at < $until");
            parameters.Add(("$until", until.ToUnixTimeSeconds()));
        }
        if (!string.IsNullOrWhiteSpace(filter.Pattern))
        {
            // Match against any of the four free-text columns. ifnull(...) -> '' so that
            // a NULL column doesn't poison the OR — the regexp function returns false on
            // null input regardless, but the explicit coalesce is clearer.
            where.Add("""
                (
                    summary REGEXP $pat
                 OR ifnull(rationale, '') REGEXP $pat
                 OR ifnull(recommended_action, '') REGEXP $pat
                 OR ifnull(evidence_json, '') REGEXP $pat
                )
                """);
            parameters.Add(("$pat", filter.Pattern));
        }
        if (!string.IsNullOrWhiteSpace(filter.ExcludePattern))
        {
            // Inverse: drop the row if ANY column matches. Wrap the same OR in a NOT.
            where.Add("""
                NOT (
                    summary REGEXP $exc
                 OR ifnull(rationale, '') REGEXP $exc
                 OR ifnull(recommended_action, '') REGEXP $exc
                 OR ifnull(evidence_json, '') REGEXP $exc
                )
                """);
            parameters.Add(("$exc", filter.ExcludePattern));
        }
        var whereSql = string.Join(" AND ", where);

        // Total count first — same WHERE, no LIMIT/OFFSET.
        var countSql = $"SELECT COUNT(*) FROM findings WHERE {whereSql};";
        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = countSql;
        foreach (var (n, v) in parameters)
        {
            countCmd.Parameters.AddWithValue(n, v);
        }
        var rawCount = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var total = rawCount switch
        {
            null or DBNull => 0,
            long l => (int)l,
            int i => i,
            _ => Convert.ToInt32(rawCount, System.Globalization.CultureInfo.InvariantCulture),
        };

        // Then the page.
        var pageSql = $"""
            SELECT id, scan_id, created_at, severity, category, source, summary, evidence_json,
                   rationale, recommended_action
            FROM findings
            WHERE {whereSql}
            ORDER BY created_at DESC
            LIMIT $limit OFFSET $offset;
            """;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = pageSql;
        foreach (var (n, v) in parameters)
        {
            cmd.Parameters.AddWithValue(n, v);
        }
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var rows = new List<Finding>(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadFinding(reader));
        }
        return new FindingsPage(rows, total);
    }

    public async Task<Finding?> GetFindingByIdAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand("""
            SELECT id, scan_id, created_at, severity, category, source, summary, evidence_json,
                   rationale, recommended_action
            FROM findings
            WHERE id = $id
            LIMIT 1;
            """,
            null,
            ("$id", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return ReadFinding(reader);
    }

    public async Task<IReadOnlyList<ScanSummary>> GetRecentScansAsync(int limit, CancellationToken ct)
    {
        if (limit <= 0)
        {
            limit = 10;
        }
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand("""
            SELECT s.id, s.started_at, s.completed_at, s.status,
                   (SELECT COUNT(*) FROM findings f WHERE f.scan_id = s.id) AS findings_count
            FROM scans s
            ORDER BY s.started_at DESC
            LIMIT $limit;
            """,
            null,
            ("$limit", limit));

        var rows = new List<ScanSummary>(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            DateTimeOffset? completed = reader.IsDBNull(2)
                ? null
                : SqliteHelpers.FromUnixSeconds(reader.GetInt64(2));
            rows.Add(new ScanSummary(
                ScanId: reader.GetString(0),
                StartedAt: SqliteHelpers.FromUnixSeconds(reader.GetInt64(1)),
                CompletedAt: completed,
                Status: reader.GetString(3),
                FindingsCount: reader.GetInt32(4)));
        }
        return rows;
    }

    /// <summary>Shared row-shape: same SELECT columns as the simple GetFindingsAsync.</summary>
    private static Finding ReadFinding(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        object? evidence = null;
        if (!reader.IsDBNull(7))
        {
            var json = reader.GetString(7);
            if (!string.IsNullOrWhiteSpace(json))
            {
                evidence = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
            }
        }
        return new Finding(
            Id: reader.GetString(0),
            ScanId: reader.GetString(1),
            CreatedAt: SqliteHelpers.FromUnixSeconds(reader.GetInt64(2)),
            Severity: Enum.Parse<Severity>(reader.GetString(3), ignoreCase: true),
            Category: Enum.Parse<FindingCategory>(reader.GetString(4), ignoreCase: true),
            Source: reader.GetString(5),
            Summary: reader.GetString(6),
            Evidence: evidence,
            Rationale: reader.IsDBNull(8) ? null : reader.GetString(8),
            RecommendedAction: reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    private static IReadOnlyList<string> AllowedSeverityNames(Severity? minSeverity)
    {
        var floor = minSeverity ?? Severity.Info;
        var list = new List<string>();
        foreach (Severity sev in Enum.GetValues<Severity>())
        {
            if (sev >= floor)
            {
                list.Add(sev.ToString());
            }
        }
        return list;
    }
}
