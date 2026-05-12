using MacMonitor.Core.Models;

namespace MacMonitor.Core.Abstractions;

/// <summary>
/// Reads/writes the <c>scans</c> and <c>findings</c> tables. Kept separate from
/// <see cref="IBaselineStore"/> to keep responsibilities narrow.
/// </summary>
public interface IScanRepository
{
    Task RecordScanStartedAsync(string scanId, DateTimeOffset startedAt, CancellationToken ct);

    Task RecordScanCompletedAsync(string scanId, DateTimeOffset completedAt, string status, CancellationToken ct);

    Task PersistFindingsAsync(IReadOnlyList<Finding> findings, CancellationToken ct);

    /// <summary>Simple list — used by the CLI <c>findings</c> subcommand.</summary>
    Task<IReadOnlyList<Finding>> GetFindingsAsync(int limit, Severity? minSeverity, CancellationToken ct);

    /// <summary>Filtered + paginated query for the Web UI's findings page.</summary>
    Task<FindingsPage> QueryFindingsAsync(FindingsFilter filter, CancellationToken ct);

    /// <summary>Look up a single finding by id, or null if not found.</summary>
    Task<Finding?> GetFindingByIdAsync(string id, CancellationToken ct);

    /// <summary>Recent scan headers + finding counts, newest first. Used by the Scan page.</summary>
    Task<IReadOnlyList<ScanSummary>> GetRecentScansAsync(int limit, CancellationToken ct);
}
