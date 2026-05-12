namespace MacMonitor.Core.Abstractions;

/// <summary>
/// Marker interface for tools the Claude agent invokes on demand during triage. Distinct
/// from <see cref="IScanTool"/> in two ways:
/// <list type="bullet">
///   <item>They are not run by the orchestrator on every scan.</item>
///   <item>They typically take parameters (a pid, a path) that the model supplies.</item>
/// </list>
/// The model never sees the underlying shell template — only the tool name and parameter
/// schema exposed via <see cref="InputJsonSchema"/>.
/// </summary>
public interface IAgentTool : ITool
{
    /// <summary>
    /// JSON schema (in the shape Anthropic's tool-use API expects under
    /// <c>input_schema</c>) describing this tool's parameters. Hand-written rather than
    /// reflected so it's easy to tune the model-facing description.
    /// </summary>
    string InputJsonSchema { get; }
}
