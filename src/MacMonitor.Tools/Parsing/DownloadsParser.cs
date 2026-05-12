using System.Globalization;
using MacMonitor.Core.Models;

namespace MacMonitor.Tools.Parsing;

/// <summary>
/// Parses pipe-delimited <c>path|epoch_mtime|size|owner|quarantine</c> emitted by the
/// recent-downloads command. The quarantine column is empty when the xattr isn't set.
/// </summary>
public static class DownloadsParser
{
    public static IReadOnlyList<DownloadedFile> Parse(string raw)
    {
        var rows = new List<DownloadedFile>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return rows;
        }
        foreach (var rawLine in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var parts = line.Split('|');
            if (parts.Length < 4)
            {
                continue;
            }
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mtime))
            {
                continue;
            }
            if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                continue;
            }
            var owner = parts[3];
            var quarantine = parts.Length >= 5 ? parts[4].Trim() : string.Empty;
            rows.Add(new DownloadedFile(
                Path: parts[0],
                ModifiedAt: DateTimeOffset.FromUnixTimeSeconds(mtime),
                SizeBytes: size,
                Owner: owner,
                QuarantineAttribute: string.IsNullOrWhiteSpace(quarantine) ? null : quarantine));
        }
        return rows;
    }
}
