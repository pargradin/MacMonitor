using System.Text.Json;
using MacMonitor.Core.Abstractions;

namespace MacMonitor.Agent;

/// <summary>
/// Translates registered <see cref="IAgentTool"/> instances into the wire-format
/// <see cref="AnthropicWire.ToolDef"/> array the Messages API expects under <c>tools</c>.
/// </summary>
internal static class ToolDefinitions
{
    public static IReadOnlyList<AnthropicWire.ToolDef> Build(IEnumerable<IAgentTool> tools)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var defs = new List<AnthropicWire.ToolDef>();
        foreach (var t in tools)
        {
            if (!IsValidName(t.Name))
            {
                throw new InvalidOperationException(
                    $"Tool name '{t.Name}' violates Anthropic's name constraint ^[a-zA-Z0-9_-]{{1,64}}$.");
            }
            if (!seen.Add(t.Name))
            {
                throw new InvalidOperationException($"Duplicate tool name: {t.Name}.");
            }
            JsonElement schema;
            try
            {
                using var doc = JsonDocument.Parse(t.InputJsonSchema);
                schema = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Tool {t.Name}: invalid input_schema JSON.", ex);
            }
            defs.Add(new AnthropicWire.ToolDef(t.Name, t.Description, schema));
        }
        return defs;
    }

    private static bool IsValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')) return false;
        }
        return true;
    }
}
