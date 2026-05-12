using MacMonitor.Core.Models;

namespace MacMonitor.Core.Abstractions;

/// <summary>
/// Tracks Anthropic API spend so the daily cap can be enforced. Backed by SQLite
/// (<c>cost_log</c> table). Implementations are safe for one writer (the worker) plus
/// any number of read-only consumers (the <c>cost</c> CLI subcommand, the Web UI).
/// </summary>
public interface ICostLedger
{
    /// <summary>Record one API call's usage and computed dollar cost.</summary>
    Task RecordAsync(AgentUsage usage, CancellationToken ct);

    /// <summary>Total USD spent today (UTC day boundary).</summary>
    Task<decimal> GetTodaySpendUsdAsync(CancellationToken ct);

    /// <summary>Today's spend + cap snapshot.</summary>
    Task<AgentBudget> GetBudgetAsync(CancellationToken ct);

    /// <summary>
    /// Per-day spend totals for the last <paramref name="days"/> UTC days, oldest first.
    /// Days with zero spend are included (so the chart doesn't have gaps).
    /// </summary>
    Task<IReadOnlyList<DailySpend>> GetRecentSpendAsync(int days, CancellationToken ct);

    /// <summary>Most recent <paramref name="limit"/> API calls, newest first.</summary>
    Task<IReadOnlyList<AgentUsage>> GetRecentCallsAsync(int limit, CancellationToken ct);
}
