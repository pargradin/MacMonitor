using System.Net;
using MacMonitor.Core.Models;

namespace MacMonitor.Worker;

/// <summary>
/// Maps a (tool, diff-operation, item) tuple to a severity and category. Matches the table
/// in PHASE2.md. Rules are deliberately conservative — Phase 3's AI agent does the nuanced
/// triage on top of these defaults.
/// </summary>
internal static class SeverityRules
{
    public static (Severity Severity, FindingCategory Category) ForAdded(string toolName, object item) =>
        toolName switch
        {
            "list_processes" => (Severity.Low, FindingCategory.Process),
            "list_launch_agents" => (Severity.Medium, FindingCategory.Persistence),
            "network_connections" => NetworkAdded(item),
            "recent_downloads" => DownloadAdded(item),
            _ => (Severity.Info, FindingCategory.System),
        };

    public static (Severity Severity, FindingCategory Category) ForRemoved(string toolName, object item) =>
        toolName switch
        {
            "list_launch_agents" => (Severity.Low, FindingCategory.Persistence),
            _ => (Severity.Info, CategoryFor(toolName)),
        };

    public static (Severity Severity, FindingCategory Category) ForChanged(string toolName, object previous, object current) =>
        toolName switch
        {
            "list_launch_agents" => (Severity.Medium, FindingCategory.Persistence),
            "recent_downloads" => DownloadChanged(previous, current),
            _ => (Severity.Low, CategoryFor(toolName)),
        };

    private static (Severity, FindingCategory) NetworkAdded(object item)
    {
        // Bump severity to Medium when the remote address is non-private.
        if (item is NetworkConnection conn && IsNonPrivateRemote(conn.RemoteAddress))
        {
            return (Severity.Medium, FindingCategory.Network);
        }
        return (Severity.Low, FindingCategory.Network);
    }

    private static (Severity, FindingCategory) DownloadAdded(object item)
    {
        // Default Info; tighten in Phase 3 once we have the verify_signature tool.
        if (item is DownloadedFile df && df.QuarantineAttribute is null && LooksExecutable(df.Path))
        {
            // Executable in Downloads with no quarantine xattr: classic Gatekeeper bypass.
            return (Severity.Medium, FindingCategory.File);
        }
        return (Severity.Info, FindingCategory.File);
    }

    private static (Severity, FindingCategory) DownloadChanged(object previous, object current)
    {
        if (previous is DownloadedFile p && current is DownloadedFile c)
        {
            // Quarantine bit going from set to unset is a strong tampering signal.
            if (p.QuarantineAttribute is not null && c.QuarantineAttribute is null)
            {
                return (Severity.Medium, FindingCategory.File);
            }
        }
        return (Severity.Low, FindingCategory.File);
    }

    private static FindingCategory CategoryFor(string toolName) =>
        toolName switch
        {
            "list_processes" => FindingCategory.Process,
            "list_launch_agents" => FindingCategory.Persistence,
            "network_connections" => FindingCategory.Network,
            "recent_downloads" => FindingCategory.File,
            _ => FindingCategory.System,
        };

    private static bool LooksExecutable(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            // Bare-name files in Downloads are uncommon and worth flagging.
            return true;
        }
        return ext.Equals(".dmg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".pkg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".app", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".sh", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".command", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonPrivateRemote(string? remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress))
        {
            return false;
        }

        // Strip any trailing port. lsof formats are usually "host:port" for IPv4 and
        // "[host]:port" for IPv6; handle both.
        var host = remoteAddress.Trim();
        if (host.StartsWith('[') && host.Contains(']'))
        {
            host = host[1..host.IndexOf(']')];
        }
        else
        {
            var lastColon = host.LastIndexOf(':');
            if (lastColon > 0 && host.IndexOf(':') == lastColon)
            {
                // Single colon → IPv4 host:port.
                host = host[..lastColon];
            }
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            // A non-numeric remote (hostname) — assume public/non-private.
            return true;
        }
        return !IsPrivate(ip);
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            // 10/8, 172.16/12, 192.168/16, 169.254/16 (link-local).
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }
        if (bytes.Length == 16)
        {
            // fc00::/7 (ULA) and fe80::/10 (link-local).
            return (bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        }
        return false;
    }
}
