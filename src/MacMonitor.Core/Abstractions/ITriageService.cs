using MacMonitor.Core.Models;

namespace MacMonitor.Core.Abstractions;

/// <summary>
/// Orchestrator-facing facade over the agent loop. Given a list of Phase-2 candidate
/// findings, returns triaged findings (severity adjusted, recommended_action filled).
/// Implementations must degrade gracefully when the budget is exhausted, the agent is
/// disabled, or the API call fails — by returning an empty list and letting the caller
/// fall back to the candidate findings unchanged.
/// </summary>
public interface ITriageService
{
    Task<IReadOnlyList<AgentTriagedFinding>> TriageAsync(
        string scanId,
        IReadOnlyList<Finding> candidates,
        CancellationToken ct);
}
