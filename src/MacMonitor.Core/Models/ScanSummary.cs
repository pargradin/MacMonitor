namespace MacMonitor.Core.Models;

/// <summary>
/// Header-only view of a scan run, used by the Web UI's recent-scans table.
/// <see cref="CompletedAt"/> is null while the scan is still in progress.
/// </summary>
public sealed record ScanSummary(
    string ScanId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    int FindingsCount);
