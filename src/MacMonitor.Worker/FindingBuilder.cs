using MacMonitor.Core.Models;

namespace MacMonitor.Worker;

/// <summary>
/// Turns a single diff item (Added / Removed / Changed) into a <see cref="Finding"/>.
/// Centralised so summary text stays consistent across tools.
/// </summary>
internal static class FindingBuilder
{
    public static Finding Baseline(string scanId, string toolName, int itemCount) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            ScanId: scanId,
            CreatedAt: DateTimeOffset.UtcNow,
            Severity: Severity.Info,
            Category: ToCategory(toolName),
            Source: toolName,
            Summary: $"{toolName}: baseline established ({itemCount} item(s)).",
            Evidence: new { itemCount });

    public static Finding Added(string scanId, string toolName, DiffItem item)
    {
        var (severity, category) = SeverityRules.ForAdded(toolName, item.Item);
        return new Finding(
            Id: Guid.NewGuid().ToString("N"),
            ScanId: scanId,
            CreatedAt: DateTimeOffset.UtcNow,
            Severity: severity,
            Category: category,
            Source: toolName,
            Summary: $"{toolName}: added — {Describe(toolName, item.Item)}",
            Evidence: new { op = "added", identity = item.IdentityKey, item = item.Item });
    }

    public static Finding Removed(string scanId, string toolName, DiffItem item)
    {
        var (severity, category) = SeverityRules.ForRemoved(toolName, item.Item);
        return new Finding(
            Id: Guid.NewGuid().ToString("N"),
            ScanId: scanId,
            CreatedAt: DateTimeOffset.UtcNow,
            Severity: severity,
            Category: category,
            Source: toolName,
            Summary: $"{toolName}: removed — {Describe(toolName, item.Item)}",
            Evidence: new { op = "removed", identity = item.IdentityKey, item = item.Item });
    }

    public static Finding Changed(string scanId, string toolName, DiffItemChange change)
    {
        var (severity, category) = SeverityRules.ForChanged(toolName, change.Previous, change.Current);
        return new Finding(
            Id: Guid.NewGuid().ToString("N"),
            ScanId: scanId,
            CreatedAt: DateTimeOffset.UtcNow,
            Severity: severity,
            Category: category,
            Source: toolName,
            Summary: $"{toolName}: changed — {Describe(toolName, change.Current)}",
            Evidence: new { op = "changed", identity = change.IdentityKey, previous = change.Previous, current = change.Current });
    }

    private static string Describe(string toolName, object item) =>
        (toolName, item) switch
        {
            ("list_processes", ProcessInfo p) => $"{TruncateCommand(p.Command)} (user={p.User}, ppid={p.ParentPid})",
            ("list_launch_agents", LaunchItem l) => $"{l.Path} (scope={l.Scope}, modified={l.ModifiedAt:u})",
            ("network_connections", NetworkConnection n) => $"{n.ProcessName} {n.Protocol} {n.LocalAddress}{(n.RemoteAddress is null ? string.Empty : " -> " + n.RemoteAddress)} ({n.State})",
            ("recent_downloads", DownloadedFile d) => $"{d.Path} (size={d.SizeBytes}, quarantine={(d.QuarantineAttribute is null ? "none" : "set")})",
            _ => item.ToString() ?? "(no details)",
        };

    private static string TruncateCommand(string command)
    {
        const int max = 160;
        return command.Length <= max ? command : command[..max] + "…";
    }

    private static FindingCategory ToCategory(string toolName) =>
        toolName switch
        {
            "list_processes" => FindingCategory.Process,
            "list_launch_agents" => FindingCategory.Persistence,
            "network_connections" => FindingCategory.Network,
            "recent_downloads" => FindingCategory.File,
            _ => FindingCategory.System,
        };
}
