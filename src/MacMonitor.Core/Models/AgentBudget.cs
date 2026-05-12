namespace MacMonitor.Core.Models;

/// <summary>
/// Snapshot of today's Anthropic spend against the configured cap. Returned by
/// <c>ICostLedger.GetBudgetAsync</c>.
/// </summary>
public sealed record AgentBudget(
    DateTimeOffset DayUtc,
    decimal SpentUsd,
    decimal CapUsd)
{
    public decimal RemainingUsd => CapUsd - SpentUsd;
    public bool IsExhausted => SpentUsd >= CapUsd;
}

/// <summary>
/// Token / cost record for a single Messages API call. Cost is computed by the agent
/// implementation using the model's pricing; this record is what gets persisted and
/// summed for the daily cap.
/// </summary>
public sealed record AgentUsage(
    DateTimeOffset OccurredAt,
    string? ScanId,
    string Model,
    int InputTokens,
    int OutputTokens,
    int CacheReadInputTokens,
    int CacheCreationInputTokens,
    decimal CostUsd);
