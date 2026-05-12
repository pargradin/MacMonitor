using System.Globalization;
using MacMonitor.Core.Models;

namespace MacMonitor.Tools.Parsing;

/// <summary>
/// Parses the delimiter-separated output of the <c>process-detail</c> command.
/// Format:
/// <code>
/// ---LSOF---
/// &lt;lsof rows&gt;
/// ---ANCESTRY---
/// &lt;pid ppid user command...&gt;  (one row per ancestor, walked up)
/// ---CODESIGN---
/// &lt;codesign output or 'no-exe'&gt;
/// </code>
/// </summary>
public static class ProcessDetailParser
{
    public static ProcessDetailPayload Parse(int pid, string raw)
    {
        var lsof = ExtractSection(raw, "---LSOF---", "---ANCESTRY---");
        var ancestryText = ExtractSection(raw, "---ANCESTRY---", "---CODESIGN---");
        var codesign = ExtractSection(raw, "---CODESIGN---", null);

        var ancestry = new List<ProcessAncestor>();
        foreach (var line in ancestryText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r').TrimStart();
            if (trimmed.Length == 0) continue;
            // Format from `ps -p $p -o pid=,ppid=,user=,command=`: 4 whitespace-delimited
            // fields, command captures the rest.
            var (a, ok) = ParseAncestorLine(trimmed);
            if (ok) ancestry.Add(a);
        }

        return new ProcessDetailPayload(pid, lsof.Trim(), ancestry, codesign.Trim());
    }

    private static (ProcessAncestor, bool) ParseAncestorLine(string line)
    {
        var pos = 0;
        var tokens = new string[3];
        for (var i = 0; i < 3; i++)
        {
            while (pos < line.Length && char.IsWhiteSpace(line[pos])) pos++;
            var start = pos;
            while (pos < line.Length && !char.IsWhiteSpace(line[pos])) pos++;
            if (start == pos) return (default!, false);
            tokens[i] = line[start..pos];
        }
        while (pos < line.Length && char.IsWhiteSpace(line[pos])) pos++;
        var command = pos < line.Length ? line[pos..] : string.Empty;

        if (!int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return (default!, false);
        if (!int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ppid)) return (default!, false);
        return (new ProcessAncestor(pid, ppid, tokens[2], command), true);
    }

    private static string ExtractSection(string raw, string startMarker, string? endMarker)
    {
        var s = raw.IndexOf(startMarker, StringComparison.Ordinal);
        if (s < 0) return string.Empty;
        s += startMarker.Length;
        if (endMarker is null)
        {
            return raw[s..];
        }
        var e = raw.IndexOf(endMarker, s, StringComparison.Ordinal);
        return e < 0 ? raw[s..] : raw[s..e];
    }
}
