namespace MacMonitor.Core.Models;

/// <summary>
/// Severity of a finding. In Phase 1 every emitted finding is <see cref="Info"/>;
/// later phases add diff-based and AI-judged severities.
/// </summary>
public enum Severity
{
    Info,
    Low,
    Medium,
    High,
}

public enum FindingCategory
{
    Process,
    Persistence,
    Network,
    File,
    System,
}

/// <summary>
/// A single observation persisted to the alert sinks. The shape is kept stable across
/// phases so downstream consumers (file tail, dashboards, the agent's later input
/// builder) don't have to be rewritten.
///
/// Phase-3 additions: <see cref="Rationale"/> (the agent's evidence-citing explanation)
/// and <see cref="RecommendedAction"/> (a string command the user can run). Both default
/// to <c>null</c>; populated only after the triage service runs successfully.
/// </summary>
public sealed record Finding(
    string Id,
    string ScanId,
    DateTimeOffset CreatedAt,
    Severity Severity,
    FindingCategory Category,
    string Source,
    string Summary,
    object? Evidence,
    string? Rationale = null,
    string? RecommendedAction = null);
