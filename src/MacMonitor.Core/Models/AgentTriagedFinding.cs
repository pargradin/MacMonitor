namespace MacMonitor.Core.Models;

/// <summary>
/// One finding after the agent's triage pass. Merged back into the corresponding
/// candidate <see cref="Finding"/> by <see cref="IdentityKey"/>.
/// </summary>
public sealed record AgentTriagedFinding(
    string IdentityKey,
    Severity Severity,
    string Summary,
    string Rationale,
    string RecommendedAction,
    IReadOnlyList<string> EvidenceRefs);
