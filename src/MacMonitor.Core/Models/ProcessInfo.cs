namespace MacMonitor.Core.Models;

/// <summary>
/// One row of <c>ps -axo pid,ppid,user,%cpu,%mem,command</c> output.
/// </summary>
public sealed record ProcessInfo(
    int Pid,
    int ParentPid,
    string User,
    double CpuPercent,
    double MemPercent,
    string Command);
