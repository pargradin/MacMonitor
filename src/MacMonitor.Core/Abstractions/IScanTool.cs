namespace MacMonitor.Core.Abstractions;

/// <summary>
/// Marker interface for tools the orchestrator runs unconditionally on every scan.
/// The four Phase-1 tools (list_processes, list_launch_agents, network_connections,
/// recent_downloads) implement this. They take no per-call arguments.
/// </summary>
public interface IScanTool : ITool
{
}
