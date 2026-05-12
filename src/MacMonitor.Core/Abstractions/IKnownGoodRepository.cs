using MacMonitor.Core.Models;

namespace MacMonitor.Core.Abstractions;

/// <summary>
/// Maintains the user-managed allow-list of (tool, identity_key) pairs that should be
/// suppressed from findings even when they appear in a diff. Backed by the <c>known_good</c>
/// table.
/// </summary>
public interface IKnownGoodRepository
{
    Task<bool> IsKnownGoodAsync(string toolName, string identityKey, CancellationToken ct);

    /// <summary>
    /// Bulk lookup. Returns the subset of <paramref name="identityKeys"/> that are
    /// allow-listed for the given tool. Used by the orchestrator to filter a diff in one call.
    /// </summary>
    Task<IReadOnlySet<string>> FilterAllowedAsync(
        string toolName,
        IReadOnlyCollection<string> identityKeys,
        CancellationToken ct);

    Task AddAsync(KnownGoodEntry entry, CancellationToken ct);

    Task<bool> RemoveAsync(string toolName, string identityKey, CancellationToken ct);

    Task<IReadOnlyList<KnownGoodEntry>> ListAsync(string? toolName, CancellationToken ct);
}
