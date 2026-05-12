using MacMonitor.Core.Models;

namespace MacMonitor.Core.Abstractions;

/// <summary>
/// Per-tool strategy that defines (a) when two records across snapshots are "the same item"
/// and (b) when an identified item has meaningfully changed. The orchestrator runs the
/// generic diff computation; the differ supplies the policy.
/// </summary>
/// <remarks>
/// Why per-tool: every tool's record type cares about different fields. <c>ProcessInfo</c>
/// keys on (command, user) because pids recycle; <c>LaunchItem</c> keys on path because
/// paths are stable; <c>NetworkConnection</c> deliberately excludes ephemeral local ports.
/// Hard-coding equality on the records themselves wouldn't generalize.
/// </remarks>
public interface IDiffer<T> : IDiffer
{
    /// <summary>Stable key that identifies "this is the same logical item" across snapshots.</summary>
    string IdentityKey(T item);

    /// <summary>
    /// Hash of the fields that, if changed, constitute a meaningful change worth emitting.
    /// Return an empty string when the differ doesn't track within-identity changes.
    /// </summary>
    string ContentHash(T item);
}

/// <summary>
/// Non-generic view used by the orchestrator, which only knows tool name (a string)
/// and previous/current payload JSON. Implementations deserialize internally to T.
/// </summary>
public interface IDiffer
{
    /// <summary>Tool name this differ applies to (matches <see cref="ITool.Name"/>).</summary>
    string ToolName { get; }

    /// <summary>Which of {Added, Removed, Changed} should produce findings.</summary>
    DiffEmissionPolicy Policy { get; }

    /// <summary>
    /// Compute a diff given the System.Text.Json serialization of the previous and current
    /// payloads. The result is the runtime-typed view (each <see cref="DiffItem.Item"/> is
    /// the original record, e.g. <c>ProcessInfo</c>) so callers can serialize evidence
    /// without needing a generic type parameter.
    /// </summary>
    DiffResult Compute(string previousJson, string currentJson);
}

[Flags]
public enum DiffEmissionPolicy
{
    None = 0,
    Added = 1 << 0,
    Removed = 1 << 1,
    Changed = 1 << 2,
    All = Added | Removed | Changed,
}
