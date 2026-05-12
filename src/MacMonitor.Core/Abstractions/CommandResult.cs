namespace MacMonitor.Core.Abstractions;

/// <summary>
/// Result of executing a single allow-listed command via the SSH executor.
/// </summary>
public sealed record CommandResult(
    string CommandId,
    int ExitStatus,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool Succeeded => ExitStatus == 0;
}
