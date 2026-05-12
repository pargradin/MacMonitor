using System.Globalization;
using MacMonitor.Core.Models;

namespace MacMonitor.Tools.Parsing;

/// <summary>
/// Parses lsof's field-tagged output (<c>-F pcPnT</c>). lsof emits records as a sequence
/// of newline-delimited "field" lines, each prefixed with a single character indicating
/// the field type:
/// <list type="bullet">
///   <item><c>p</c>: pid (starts a new process record)</item>
///   <item><c>c</c>: command name</item>
///   <item><c>f</c>: file descriptor (starts a new fd record under the current process)</item>
///   <item><c>P</c>: protocol</item>
///   <item><c>n</c>: name (host:port[-&gt;host:port])</item>
///   <item><c>T</c>: type info, including <c>ST=</c>state for TCP</item>
/// </list>
/// </summary>
public static class LsofParser
{
    public static IReadOnlyList<NetworkConnection> Parse(string raw)
    {
        var rows = new List<NetworkConnection>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return rows;
        }

        var pid = 0;
        var command = string.Empty;
        string? proto = null;
        string? name = null;
        string? state = null;

        void EmitIfReady()
        {
            if (pid != 0 && proto is not null && name is not null)
            {
                var (local, remote) = SplitName(name);
                rows.Add(new NetworkConnection(
                    Pid: pid,
                    ProcessName: command,
                    Protocol: proto,
                    LocalAddress: local,
                    RemoteAddress: remote,
                    State: state ?? string.Empty));
            }
            proto = null;
            name = null;
            state = null;
        }

        foreach (var rawLine in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 2)
            {
                continue;
            }
            var tag = line[0];
            var value = line[1..];
            switch (tag)
            {
                case 'p':
                    EmitIfReady();
                    pid = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
                    break;
                case 'c':
                    command = value;
                    break;
                case 'f':
                    EmitIfReady();
                    break;
                case 'P':
                    proto = value;
                    break;
                case 'n':
                    name = value;
                    break;
                case 'T':
                    if (value.StartsWith("ST=", StringComparison.Ordinal))
                    {
                        state = value[3..];
                    }
                    break;
            }
        }
        EmitIfReady();
        return rows;
    }

    private static (string Local, string? Remote) SplitName(string name)
    {
        var arrow = name.IndexOf("->", StringComparison.Ordinal);
        return arrow < 0 ? (name, null) : (name[..arrow], name[(arrow + 2)..]);
    }
}
