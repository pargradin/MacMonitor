using MacMonitor.Core.Models;

namespace MacMonitor.Core.Abstractions;

/// <summary>
/// Persists per-(scan, tool) snapshots and serves the most recent snapshot for diffing.
/// Implementations are expected to be safe for one writer (the worker) and any number
/// of read-only consumers (CLI subcommands, future Blazor UI).
/// </summary>
public interface IBaselineStore
{
    /// <summary>
    /// Initialise the underlying storage (create DB file, run pending migrations, etc.).
    /// Idempotent. Should be called once during host startup.
    /// </summary>
    Task InitializeAsync(CancellationToken ct);

    /// <summary>
    /// Returns the most recent snapshot for the given tool, or <c>null</c> if there is none
    /// (cold start). The orchestrator uses <c>null</c> to mean "treat this scan as baseline."
    /// </summary>
    Task<Snapshot?> GetLatestSnapshotAsync(string toolName, CancellationToken ct);

    /// <summary>
    /// Persist the snapshot captured during a scan. Replaces the existing row for
    /// (scan_id, tool_name) if present (idempotent re-runs).
    /// </summary>
    Task SaveSnapshotAsync(Snapshot snapshot, CancellationToken ct);

    /// <summary>
    /// Drop snapshots older than the configured retention window, keeping at least
    /// <c>RetentionSnapshotsPerTool</c> per tool. Run at the end of each scan.
    /// </summary>
    Task ApplyRetentionAsync(CancellationToken ct);
}
