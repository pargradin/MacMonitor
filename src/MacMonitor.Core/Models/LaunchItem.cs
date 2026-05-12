namespace MacMonitor.Core.Models;

/// <summary>
/// A persistence entry on disk: a launchd plist in one of the four standard scopes.
/// In Phase 1 we capture the file metadata only; reading and parsing the plist
/// content is added as a follow-up tool.
/// </summary>
public sealed record LaunchItem(
    string Path,
    LaunchScope Scope,
    DateTimeOffset ModifiedAt,
    long SizeBytes);

public enum LaunchScope
{
    /// <summary>/Library/LaunchAgents — runs for any user that logs in.</summary>
    SystemUserAgents,

    /// <summary>~/Library/LaunchAgents — runs for the current user only.</summary>
    UserAgents,

    /// <summary>/Library/LaunchDaemons — runs as root, system-wide.</summary>
    SystemDaemons,

    /// <summary>/System/Library/LaunchDaemons — Apple-managed daemons (read-only on SIP-protected paths).</summary>
    AppleDaemons,
}
