using MacMonitor.Core.Abstractions;

namespace MacMonitor.Core.Models;

/// <summary>
/// Aggregated output of one scan run.
/// </summary>
public sealed record ScanResult(
    string ScanId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<ToolResult> ToolResults,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> Errors)
{
    public TimeSpan Duration => CompletedAt - StartedAt;
}
