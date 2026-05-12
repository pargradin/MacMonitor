using System.Globalization;
using MacMonitor.Core.Models;

namespace MacMonitor.Tools.Parsing;

/// <summary>
/// Parses output of <c>ps -axww -o pid=,ppid=,user=,%cpu=,%mem=,command=</c>.
/// First five whitespace-delimited tokens are fixed-width fields; everything after
/// is the command (which itself may contain spaces).
/// </summary>
public static class PsParser
{
    public static IReadOnlyList<ProcessInfo> Parse(string raw)
    {
        var rows = new List<ProcessInfo>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return rows;
        }

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r').TrimStart();
            if (trimmed.Length == 0)
            {
                continue;
            }
            if (TryParseLine(trimmed, out var info))
            {
                rows.Add(info);
            }
        }
        return rows;
    }

    private static bool TryParseLine(string line, out ProcessInfo info)
    {
        info = null!;
        // Take first 5 whitespace-delimited tokens, rest is the command.
        var tokens = new string[5];
        var idx = 0;
        var pos = 0;
        for (var i = 0; i < 5; i++)
        {
            while (pos < line.Length && char.IsWhiteSpace(line[pos]))
            {
                pos++;
            }
            var start = pos;
            while (pos < line.Length && !char.IsWhiteSpace(line[pos]))
            {
                pos++;
            }
            if (start == pos)
            {
                return false;
            }
            tokens[idx++] = line[start..pos];
        }
        while (pos < line.Length && char.IsWhiteSpace(line[pos]))
        {
            pos++;
        }
        var command = pos < line.Length ? line[pos..] : string.Empty;

        if (!int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return false;
        if (!int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ppid)) return false;
        var user = tokens[2];
        if (!double.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu)) cpu = 0;
        if (!double.TryParse(tokens[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var mem)) mem = 0;

        info = new ProcessInfo(pid, ppid, user, cpu, mem, command);
        return true;
    }
}
