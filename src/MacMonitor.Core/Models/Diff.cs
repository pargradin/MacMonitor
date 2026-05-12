namespace MacMonitor.Core.Models;

/// <summary>
/// Result of comparing two snapshots through an <c>IDiffer&lt;T&gt;</c>. The strongly-typed
/// form, returned by the generic <c>DiffEngine.Compute&lt;T&gt;</c>.
/// </summary>
public sealed record Diff<T>(
    IReadOnlyList<T> Added,
    IReadOnlyList<T> Removed,
    IReadOnlyList<DiffChange<T>> Changed)
{
    public static Diff<T> Empty { get; } = new(
        Array.Empty<T>(),
        Array.Empty<T>(),
        Array.Empty<DiffChange<T>>());

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;

    public int TotalChanges => Added.Count + Removed.Count + Changed.Count;
}

/// <summary>
/// An item identified across two snapshots whose <c>ContentHash</c> changed.
/// </summary>
public sealed record DiffChange<T>(string IdentityKey, T Previous, T Current);

/// <summary>
/// Runtime-typed view of a diff, returned by <c>IDiffer.Compute</c>. Each item is boxed
/// as <see cref="object"/> so the orchestrator can iterate without knowing the per-tool
/// record type. <c>System.Text.Json</c> serialization of the boxed items still produces
/// the right shape because it inspects the runtime type.
/// </summary>
public sealed record DiffResult(
    IReadOnlyList<DiffItem> Added,
    IReadOnlyList<DiffItem> Removed,
    IReadOnlyList<DiffItemChange> Changed)
{
    public static DiffResult Empty { get; } = new(
        Array.Empty<DiffItem>(),
        Array.Empty<DiffItem>(),
        Array.Empty<DiffItemChange>());

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;

    public int TotalChanges => Added.Count + Removed.Count + Changed.Count;
}

public sealed record DiffItem(string IdentityKey, object Item);

public sealed record DiffItemChange(string IdentityKey, object Previous, object Current);
