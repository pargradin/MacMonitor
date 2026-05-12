namespace MacMonitor.Core.Models;

/// <summary>
/// A single network endpoint observed by <c>lsof -nP -iTCP -iUDP</c>.
/// </summary>
public sealed record NetworkConnection(
    int Pid,
    string ProcessName,
    string Protocol,
    string LocalAddress,
    string? RemoteAddress,
    string State);
