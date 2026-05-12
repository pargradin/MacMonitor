using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MacMonitor.Storage;

/// <summary>
/// SQLite-backed <see cref="ICostLedger"/>. Reads/writes the <c>cost_log</c> table.
///
/// The daily cap value comes through the constructor as a no-arg <see cref="Func{TResult}"/>
/// rather than as a direct dependency on <c>AgentOptions</c> — this keeps the Storage
/// project free of any reference to <c>MacMonitor.Agent</c> (the dependency direction is
/// Agent → Storage, not the other way around). The Worker wires the func at startup.
/// </summary>
public sealed class SqliteCostLedger : ICostLedger
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<SqliteCostLedger> _logger;
    private readonly Func<decimal> _capUsdResolver;

    public SqliteCostLedger(
        SqliteConnectionFactory factory,
        ILogger<SqliteCostLedger> logger,
        Func<decimal>? capUsdResolver = null)
    {
        _factory = factory;
        _logger = logger;
        // Default cap is "no cap" so the ledger is safe to use in contexts that haven't
        // wired the resolver (tests, the Phase-2 path that doesn't yet know about Agent).
        _capUsdResolver = capUsdResolver ?? (() => decimal.MaxValue);
    }

    public async Task RecordAsync(AgentUsage usage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(usage);
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("""
            INSERT INTO cost_log
                (occurred_at, scan_id, model, input_tokens, output_tokens,
                 cache_read_input_tokens, cache_creation_input_tokens, cost_usd)
            VALUES ($ts, $scan, $model, $in, $out, $cr, $cw, $cost);
            """,
            null, ct,
            ("$ts", usage.OccurredAt.ToUnixTimeSeconds()),
            ("$scan", (object?)usage.ScanId ?? DBNull.Value),
            ("$model", usage.Model),
            ("$in", usage.InputTokens),
            ("$out", usage.OutputTokens),
            ("$cr", usage.CacheReadInputTokens),
            ("$cw", usage.CacheCreationInputTokens),
            ("$cost", (double)usage.CostUsd)).ConfigureAwait(false);

        _logger.LogDebug("Cost ledger: {Model} +{In}/{Out} tok = ${Cost:F4}.",
            usage.Model, usage.InputTokens, usage.OutputTokens, usage.CostUsd);
    }

    public async Task<decimal> GetTodaySpendUsdAsync(CancellationToken ct)
    {
        var startOfDay = StartOfTodayUtc().ToUnixTimeSeconds();
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var raw = await conn.ExecuteScalarAsync("""
            SELECT COALESCE(SUM(cost_usd), 0) FROM cost_log WHERE occurred_at >= $start;
            """,
            null, ct,
            ("$start", startOfDay)).ConfigureAwait(false);

        // SQLite returns REAL → double when there are rows, or the literal 0 (long) when COALESCE
        // hits the empty case. Handle both.
        return raw switch
        {
            null or DBNull => 0m,
            double d => (decimal)d,
            long l => (decimal)l,
            decimal m => m,
            _ => Convert.ToDecimal(raw, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    public async Task<AgentBudget> GetBudgetAsync(CancellationToken ct)
    {
        var spent = await GetTodaySpendUsdAsync(ct).ConfigureAwait(false);
        var cap = _capUsdResolver();
        return new AgentBudget(StartOfTodayUtc(), spent, cap);
    }

    public async Task<IReadOnlyList<DailySpend>> GetRecentSpendAsync(int days, CancellationToken ct)
    {
        if (days <= 0) days = 7;

        // Build the day buckets in C# rather than SQL — SQLite's date arithmetic with
        // unix seconds is doable but ugly, and we want a row for empty days too.
        var today = StartOfTodayUtc();
        var since = today.AddDays(-(days - 1));

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand("""
            SELECT occurred_at, cost_usd
            FROM cost_log
            WHERE occurred_at >= $since;
            """,
            null,
            ("$since", since.ToUnixTimeSeconds()));

        // Pre-fill every day with zero so the chart never has gaps.
        var buckets = new Dictionary<DateTimeOffset, decimal>();
        for (var i = 0; i < days; i++)
        {
            buckets[since.AddDays(i)] = 0m;
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var ts = SqliteHelpers.FromUnixSeconds(reader.GetInt64(0));
            var dayKey = new DateTimeOffset(ts.Year, ts.Month, ts.Day, 0, 0, 0, TimeSpan.Zero);
            var cost = reader.GetDouble(1);
            if (buckets.ContainsKey(dayKey))
            {
                buckets[dayKey] += (decimal)cost;
            }
        }

        return buckets
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => new DailySpend(kvp.Key, kvp.Value))
            .ToList();
    }

    public async Task<IReadOnlyList<AgentUsage>> GetRecentCallsAsync(int limit, CancellationToken ct)
    {
        if (limit <= 0) limit = 50;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand("""
            SELECT occurred_at, scan_id, model, input_tokens, output_tokens,
                   cache_read_input_tokens, cache_creation_input_tokens, cost_usd
            FROM cost_log
            ORDER BY occurred_at DESC
            LIMIT $limit;
            """,
            null,
            ("$limit", limit));

        var rows = new List<AgentUsage>(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new AgentUsage(
                OccurredAt: SqliteHelpers.FromUnixSeconds(reader.GetInt64(0)),
                ScanId: reader.IsDBNull(1) ? null : reader.GetString(1),
                Model: reader.GetString(2),
                InputTokens: reader.GetInt32(3),
                OutputTokens: reader.GetInt32(4),
                CacheReadInputTokens: reader.GetInt32(5),
                CacheCreationInputTokens: reader.GetInt32(6),
                CostUsd: (decimal)reader.GetDouble(7)));
        }
        return rows;
    }

    private static DateTimeOffset StartOfTodayUtc()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
    }
}
