using System.Globalization;
using MacMonitor.Core.Models;

namespace MacMonitor.Tools.Parsing;

/// <summary>
/// Parses pipe-delimited output of the <c>quarantine-events</c> SQL query.
/// Columns: LSQuarantineTimeStamp | LSQuarantineAgentName | LSQuarantineDataURLString | LSQuarantineOriginURLString.
///
/// LSQuarantineTimeStamp is a Core Data / NSDate "absolute time" (seconds since 2001-01-01 UTC).
/// </summary>
public static class QuarantineParser
{
    private static readonly DateTimeOffset CoreDataEpoch =
        new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static QuarantineEventsPayload Parse(string raw)
    {
        var events = new List<QuarantineEvent>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new QuarantineEventsPayload(events);
        }

        foreach (var rawLine in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            if (parts.Length < 4) continue;

            DateTimeOffset ts;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                ts = CoreDataEpoch.AddSeconds(seconds);
            }
            else
            {
                ts = DateTimeOffset.MinValue;
            }
            events.Add(new QuarantineEvent(ts, parts[1], parts[2], parts[3]));
        }
        return new QuarantineEventsPayload(events);
    }
}
