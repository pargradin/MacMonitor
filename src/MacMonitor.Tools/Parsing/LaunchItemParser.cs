using System.Globalization;
using MacMonitor.Core.Models;

namespace MacMonitor.Tools.Parsing;

/// <summary>
/// Parses lines of <c>path|epoch_mtime|size_bytes</c> emitted by the list-launch-items
/// command. Path is mapped to a <see cref="LaunchScope"/> by prefix.
/// </summary>
public static class LaunchItemParser
{
    public static IReadOnlyList<LaunchItem> Parse(string raw, string homeDir)
    {
        var items = new List<LaunchItem>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return items;
        }
        var userAgentsPrefix = Path.Combine(homeDir, "Library", "LaunchAgents");

        foreach (var rawLine in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var parts = line.Split('|');
            if (parts.Length < 3)
            {
                continue;
            }
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            {
                continue;
            }
            if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                continue;
            }
            var path = parts[0];
            var scope = ScopeOf(path, userAgentsPrefix);
            items.Add(new LaunchItem(
                Path: path,
                Scope: scope,
                ModifiedAt: DateTimeOffset.FromUnixTimeSeconds(epoch),
                SizeBytes: size));
        }
        return items;
    }

    private static LaunchScope ScopeOf(string path, string userAgentsPrefix)
    {
        if (path.StartsWith(userAgentsPrefix, StringComparison.Ordinal)) return LaunchScope.UserAgents;
        if (path.StartsWith("/Library/LaunchAgents", StringComparison.Ordinal)) return LaunchScope.SystemUserAgents;
        if (path.StartsWith("/Library/LaunchDaemons", StringComparison.Ordinal)) return LaunchScope.SystemDaemons;
        if (path.StartsWith("/System/Library/LaunchDaemons", StringComparison.Ordinal)) return LaunchScope.AppleDaemons;
        // Fallback: best guess by name.
        return path.Contains("LaunchDaemons", StringComparison.Ordinal)
            ? LaunchScope.SystemDaemons
            : LaunchScope.UserAgents;
    }
}
