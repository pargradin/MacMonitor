using MacMonitor.Core.Models;

namespace MacMonitor.Tools.Parsing;

/// <summary>
/// Parses the delimited output of the <c>verify-signature</c> command.
/// </summary>
public static class SignatureParser
{
    public static SignaturePayload Parse(string path, string raw)
    {
        var codesignText = ExtractSection(raw, "---CODESIGN---", "---SPCTL---");
        var spctlText = ExtractSection(raw, "---SPCTL---", null);

        string? identifier = null;
        string? teamId = null;
        var authorityChain = new List<string>();
        var verified = !codesignText.Contains("code object is not signed", StringComparison.OrdinalIgnoreCase)
                    && !codesignText.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                    && !codesignText.Contains("not signed at all", StringComparison.OrdinalIgnoreCase);

        foreach (var rawLine in codesignText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.StartsWith("Identifier=", StringComparison.Ordinal))
            {
                identifier = line["Identifier=".Length..];
            }
            else if (line.StartsWith("TeamIdentifier=", StringComparison.Ordinal))
            {
                teamId = line["TeamIdentifier=".Length..];
            }
            else if (line.StartsWith("Authority=", StringComparison.Ordinal))
            {
                authorityChain.Add(line["Authority=".Length..]);
            }
        }

        // spctl one-liner forms: "<path>: accepted" / "<path>: rejected" plus optional "source=..." line.
        var accepted = false;
        string? source = null;
        foreach (var rawLine in spctlText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.EndsWith(": accepted", StringComparison.Ordinal))
            {
                accepted = true;
            }
            else if (line.StartsWith("source=", StringComparison.Ordinal))
            {
                source = line["source=".Length..];
            }
        }

        return new SignaturePayload(path, verified, identifier, teamId, authorityChain, accepted, source, raw);
    }

    private static string ExtractSection(string raw, string startMarker, string? endMarker)
    {
        var s = raw.IndexOf(startMarker, StringComparison.Ordinal);
        if (s < 0) return string.Empty;
        s += startMarker.Length;
        if (endMarker is null) return raw[s..];
        var e = raw.IndexOf(endMarker, s, StringComparison.Ordinal);
        return e < 0 ? raw[s..] : raw[s..e];
    }
}
