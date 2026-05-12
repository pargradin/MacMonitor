using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacMonitor.Agent;

/// <summary>
/// Wire-format DTOs for Anthropic's <c>POST /v1/messages</c> endpoint. Internal to this
/// project — the Core abstractions only see <c>AgentTriagedFinding</c> / <c>AgentUsage</c>.
/// </summary>
public static class AnthropicWire
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public sealed record MessageRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] IReadOnlyList<Message> Messages,
        [property: JsonPropertyName("tools")] IReadOnlyList<ToolDef>? Tools);

    /// <summary>
    /// One conversation turn. <see cref="Content"/> is always an array of typed blocks
    /// (we never use the simpler string-content shorthand) so the loop can mix
    /// text / tool_use / tool_result blocks freely.
    /// </summary>
    public sealed record Message(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] IReadOnlyList<ContentBlock> Content);

    /// <summary>
    /// Polymorphic content block. Only the fields relevant to <see cref="Type"/> are set;
    /// the others are <c>null</c> and are dropped from the wire JSON via the global
    /// <c>WhenWritingNull</c> policy.
    /// </summary>
    public sealed record ContentBlock(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text = null,
        [property: JsonPropertyName("id")] string? Id = null,
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("input")] JsonElement? Input = null,
        [property: JsonPropertyName("tool_use_id")] string? ToolUseId = null,
        [property: JsonPropertyName("content")] string? Content = null,
        [property: JsonPropertyName("is_error")] bool? IsError = null);

    public sealed record ToolDef(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("input_schema")] JsonElement InputSchema);

    public sealed record MessageResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("stop_reason")] string StopReason,
        [property: JsonPropertyName("content")] IReadOnlyList<ContentBlock> Content,
        [property: JsonPropertyName("usage")] Usage Usage);

    public sealed record Usage(
        [property: JsonPropertyName("input_tokens")] int InputTokens,
        [property: JsonPropertyName("output_tokens")] int OutputTokens,
        [property: JsonPropertyName("cache_read_input_tokens")] int? CacheReadInputTokens,
        [property: JsonPropertyName("cache_creation_input_tokens")] int? CacheCreationInputTokens);
}
