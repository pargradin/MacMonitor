namespace MacMonitor.Core.Abstractions;

/// <summary>
/// An inspection capability. In Phase 0/1 each ITool just runs one or more allow-listed
/// commands and returns a structured payload. In Phase 3 these same tools become the
/// surface area exposed to the Claude agent.
/// </summary>
public interface ITool
{
    /// <summary>Stable identifier — also the name shown to the AI agent later.</summary>
    string Name { get; }

    /// <summary>Human-readable description, also reused as the agent-facing description.</summary>
    string Description { get; }

    /// <summary>
    /// Execute the tool against a connected SSH session and return its parsed payload.
    /// </summary>
    Task<ToolResult> ExecuteAsync(
        ISshExecutor ssh,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct);
}

/// <summary>
/// A typed envelope around a tool's parsed output. <see cref="Payload"/> is the structured
/// data; <see cref="RawOutput"/> is kept for diagnostics and future hashing/diffing.
/// </summary>
public sealed record ToolResult(
    string ToolName,
    object Payload,
    string RawOutput,
    TimeSpan Duration,
    IReadOnlyList<string> Warnings)
{
    public static ToolResult Of<T>(string toolName, T payload, string raw, TimeSpan dur, IReadOnlyList<string>? warnings = null)
        where T : class
        => new(toolName, payload, raw, dur, warnings ?? Array.Empty<string>());
}
