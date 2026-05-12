using System.Net;
using MacMonitor.Core.Models;

namespace MacMonitor.Worker;

/// <summary>
/// Cold-start triage helper. On the very first scan of a tool, we don't have a previous
/// snapshot to diff against — the entire current state is "the baseline." Most of it is
/// benign (Apple-signed daemons, system processes, loopback sockets). A small subset is
/// worth showing the agent: third-party persistence entries, processes spawned from
/// non-system paths, listeners on non-loopback addresses, recent unsigned downloads.
///
/// This class encodes those rules. Each method returns a sequence of <see cref="DiffItem"/>
/// whose <c>IdentityKey</c> matches what the corresponding <c>IDiffer</c> would produce —
/// so once these findings are persisted (and the snapshot stored), the next scan's diff
/// will see them as "already known" and not re-emit them.
///
/// The heuristics are intentionally conservative: better to send the agent ten items
/// worth a second look than five hundred items it has to sift through. False negatives
/// here just mean a finding only fires once a real diff happens.
/// </summary>
internal static class BaselineHeuristics
{
    public static IEnumerable<DiffItem> FindSuspicious(string toolName, object payload) =>
        (toolName, payload) switch
        {
            ("list_processes", IReadOnlyList<ProcessInfo> ps) => FindSuspiciousProcesses(ps),
            ("list_launch_agents", IReadOnlyList<LaunchItem> li) => FindSuspiciousLaunchItems(li),
            ("network_connections", IReadOnlyList<NetworkConnection> nc) => FindSuspiciousConnections(nc),
            ("recent_downloads", IReadOnlyList<DownloadedFile> df) => FindSuspiciousDownloads(df),
            _ => Array.Empty<DiffItem>(),
        };

    // ──────────────── Processes ────────────────

    private static IEnumerable<DiffItem> FindSuspiciousProcesses(IReadOnlyList<ProcessInfo> processes)
    {
        foreach (var p in processes)
        {
            if (IsLikelyBenignProcess(p)) continue;
            // Identity must match ProcessInfoDiffer.IdentityKey: "<command>@<user>".
            yield return new DiffItem(
                IdentityKey: $"{p.Command}@{p.User}",
                Item: p);
        }
    }

    private static bool IsLikelyBenignProcess(ProcessInfo p)
    {
        if (string.IsNullOrWhiteSpace(p.Command)) return true; // kernel threads / accounting rows

        // The first whitespace-delimited token is the executable path. Apple-shipped or
        // /Applications-installed processes are overwhelmingly benign at baseline.
        var firstSpace = p.Command.IndexOf(' ');
        var path = firstSpace > 0 ? p.Command[..firstSpace] : p.Command;

        return path.StartsWith("/System/", StringComparison.Ordinal)
            || path.StartsWith("/usr/", StringComparison.Ordinal)
            || path.StartsWith("/sbin/", StringComparison.Ordinal)
            || path.StartsWith("/bin/", StringComparison.Ordinal)
            || path.StartsWith("/Applications/", StringComparison.Ordinal)
            || path.StartsWith("/Library/Apple/", StringComparison.Ordinal)
            // ps sometimes formats kernel threads or special procs in brackets.
            || path.StartsWith("[", StringComparison.Ordinal);
    }

    // ──────────────── Launch items ────────────────

    private static IEnumerable<DiffItem> FindSuspiciousLaunchItems(IReadOnlyList<LaunchItem> items)
    {
        foreach (var item in items)
        {
            if (IsLikelyBenignLaunchItem(item)) continue;
            // Identity must match LaunchItemDiffer.IdentityKey: the path.
            yield return new DiffItem(IdentityKey: item.Path, Item: item);
        }
    }

    private static bool IsLikelyBenignLaunchItem(LaunchItem item)
    {
        // Apple's own daemons are SIP-protected; assume benign.
        if (item.Scope == LaunchScope.AppleDaemons) return true;
        // Plists whose filename starts with com.apple. are almost always Apple-shipped
        // even when they live outside /System/Library (e.g. com.apple.helpd).
        var name = Path.GetFileNameWithoutExtension(item.Path);
        if (name.StartsWith("com.apple.", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ──────────────── Network connections ────────────────

    private static IEnumerable<DiffItem> FindSuspiciousConnections(IReadOnlyList<NetworkConnection> conns)
    {
        foreach (var c in conns)
        {
            if (c.RemoteAddress is null)
            {
                // Listening socket — interesting only when bound to a non-loopback address.
                if (IsLoopbackAddress(c.LocalAddress)) continue;
                var port = ExtractPort(c.LocalAddress);
                yield return new DiffItem(
                    // Match NetworkConnectionDiffer.IdentityKey for listeners.
                    IdentityKey: $"{c.ProcessName}|{c.Protocol}|LISTEN:{port}",
                    Item: c);
            }
            else
            {
                // Established / outbound — interesting only when the remote is non-private.
                if (IsPrivateOrLoopback(c.RemoteAddress)) continue;
                yield return new DiffItem(
                    IdentityKey: $"{c.ProcessName}|{c.Protocol}|{c.RemoteAddress}",
                    Item: c);
            }
        }
    }

    private static string ExtractPort(string address)
    {
        // Mirror NetworkConnectionDiffer's logic — last colon wins.
        var idx = address.LastIndexOf(':');
        return idx >= 0 ? address[(idx + 1)..] : address;
    }

    private static bool IsLoopbackAddress(string address)
    {
        // Strip the trailing :port, then test.
        var host = StripPort(address);
        if (string.IsNullOrEmpty(host)) return false;
        if (host.Equals("127.0.0.1", StringComparison.Ordinal)) return true;
        if (host.Equals("::1", StringComparison.Ordinal)) return true;
        if (host.Equals("*", StringComparison.Ordinal)) return false;     // wildcard bind = listen on all interfaces
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    private static bool IsPrivateOrLoopback(string remoteAddress)
    {
        var host = StripPort(remoteAddress);
        if (string.IsNullOrEmpty(host)) return false;
        if (!IPAddress.TryParse(host, out var ip)) return false; // hostname → can't tell, assume not private
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            // RFC 1918 + link-local.
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }
        if (bytes.Length == 16)
        {
            return (bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        }
        return false;
    }

    private static string StripPort(string address)
    {
        var trimmed = address.Trim();
        if (trimmed.StartsWith('[') && trimmed.Contains(']'))
        {
            // [::1]:80 → ::1
            return trimmed[1..trimmed.IndexOf(']')];
        }
        var lastColon = trimmed.LastIndexOf(':');
        if (lastColon > 0 && trimmed.IndexOf(':') == lastColon)
        {
            // Single colon — IPv4 host:port.
            return trimmed[..lastColon];
        }
        return trimmed;
    }

    // ──────────────── Downloads ────────────────

    private static IEnumerable<DiffItem> FindSuspiciousDownloads(IReadOnlyList<DownloadedFile> files)
    {
        var oneWeekAgo = DateTimeOffset.UtcNow.AddDays(-7);
        foreach (var f in files)
        {
            // Two flag conditions:
            // 1. Looks executable AND no quarantine xattr — classic Gatekeeper bypass shape.
            // 2. Looks executable AND modified in the last week — even quarantined, recent
            //    executables in Downloads warrant a look at baseline time.
            if (!LooksExecutable(f.Path)) continue;
            var noQuarantine = f.QuarantineAttribute is null;
            var recent = f.ModifiedAt > oneWeekAgo;
            if (!noQuarantine && !recent) continue;

            yield return new DiffItem(IdentityKey: f.Path, Item: f);
        }
    }

    private static bool LooksExecutable(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            // Bare-name files in Downloads are uncommon; flag.
            return true;
        }
        return ext.Equals(".dmg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".pkg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".app", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".sh", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".command", StringComparison.OrdinalIgnoreCase);
    }
}
