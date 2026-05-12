namespace MacMonitor.Core.Models;

/// <summary>
/// Typed payloads returned by the five Phase-3 detail tools. Kept in Core so both the
/// tools project (where they're produced) and any future consumer (a Blazor UI, the agent
/// project for input shaping) can deserialize them.
/// </summary>
public sealed record ProcessDetailPayload(
    int Pid,
    string LsofText,
    IReadOnlyList<ProcessAncestor> Ancestry,
    string CodesignText);

public sealed record ProcessAncestor(int Pid, int ParentPid, string User, string Command);

public sealed record LaunchPlistPayload(
    string Path,
    string? Label,
    IReadOnlyList<string> ProgramArguments,
    bool? RunAtLoad,
    bool? KeepAlive,
    string? ProcessType,
    string? UserName,
    IReadOnlyDictionary<string, string> ExtraKeysRaw);

public sealed record SignaturePayload(
    string Path,
    bool Verified,
    string? Identifier,
    string? TeamIdentifier,
    IReadOnlyList<string> AuthorityChain,
    bool Accepted,
    string? Source,
    string RawText);

public sealed record HashPayload(string Path, string Sha256);

public sealed record QuarantineEvent(
    DateTimeOffset Timestamp,
    string AgentName,
    string DataUrl,
    string OriginUrl);

public sealed record QuarantineEventsPayload(IReadOnlyList<QuarantineEvent> Events);
