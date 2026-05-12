using System.Xml;
using System.Xml.Linq;
using MacMonitor.Core.Models;

namespace MacMonitor.Tools.Parsing;

/// <summary>
/// Parses the XML form of a launchd plist (output of <c>plutil -convert xml1 -o -</c>)
/// into a <see cref="LaunchPlistPayload"/>.
///
/// We only model the keys that matter for triage (Label, ProgramArguments, RunAtLoad,
/// KeepAlive, ProcessType, UserName); everything else is stuffed into
/// <see cref="LaunchPlistPayload.ExtraKeysRaw"/> as serialized strings so the agent can
/// see it without us having to model every possible plist key.
/// </summary>
public static class PlistParser
{
    public static LaunchPlistPayload Parse(string path, string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return new LaunchPlistPayload(path, null, Array.Empty<string>(), null, null, null, null,
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException)
        {
            return new LaunchPlistPayload(path, null, Array.Empty<string>(), null, null, null, null,
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var topDict = doc.Root?.Element("dict");
        if (topDict is null)
        {
            return new LaunchPlistPayload(path, null, Array.Empty<string>(), null, null, null, null,
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        string? label = null;
        var programArguments = new List<string>();
        bool? runAtLoad = null;
        bool? keepAlive = null;
        string? processType = null;
        string? userName = null;
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, valueElement) in IterateDictPairs(topDict))
        {
            switch (key)
            {
                case "Label":
                    label = valueElement.Value;
                    break;
                case "ProgramArguments":
                    if (valueElement.Name.LocalName == "array")
                    {
                        foreach (var s in valueElement.Elements("string"))
                        {
                            programArguments.Add(s.Value);
                        }
                    }
                    break;
                case "RunAtLoad":
                    runAtLoad = ParseBool(valueElement);
                    break;
                case "KeepAlive":
                    // KeepAlive can be a bool or a dict — capture either.
                    keepAlive = ParseBool(valueElement);
                    if (keepAlive is null && valueElement.Name.LocalName == "dict")
                    {
                        keepAlive = true;
                        extra["KeepAlive"] = valueElement.ToString(SaveOptions.DisableFormatting);
                    }
                    break;
                case "ProcessType":
                    processType = valueElement.Value;
                    break;
                case "UserName":
                    userName = valueElement.Value;
                    break;
                default:
                    extra[key] = valueElement.ToString(SaveOptions.DisableFormatting);
                    break;
            }
        }

        return new LaunchPlistPayload(
            Path: path,
            Label: label,
            ProgramArguments: programArguments,
            RunAtLoad: runAtLoad,
            KeepAlive: keepAlive,
            ProcessType: processType,
            UserName: userName,
            ExtraKeysRaw: extra);
    }

    /// <summary>
    /// Iterate the immediate children of a plist &lt;dict&gt; as (key, valueElement) pairs.
    /// plist's quirk: keys and values are sibling elements, not nested.
    /// </summary>
    private static IEnumerable<(string Key, XElement Value)> IterateDictPairs(XElement dict)
    {
        XElement? pendingKey = null;
        foreach (var child in dict.Elements())
        {
            if (child.Name.LocalName == "key")
            {
                pendingKey = child;
                continue;
            }
            if (pendingKey is not null)
            {
                yield return (pendingKey.Value, child);
                pendingKey = null;
            }
        }
    }

    private static bool? ParseBool(XElement el) =>
        el.Name.LocalName switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
}
