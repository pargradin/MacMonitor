using System.Text.Json;
using System.Text.Json.Serialization;
using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MacMonitor.Agent;

/// <summary>
/// The bounded tool-use loop. Sends an initial user message containing the candidate
/// findings, follows tool_use blocks by dispatching to the matching <see cref="IAgentTool"/>
/// over the existing SSH session, appends tool_result blocks, and loops until the model
/// emits a final report (stop_reason = "end_turn") or a bound is hit.
/// </summary>
public sealed class AgentLoop
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AnthropicClient _client;
    private readonly ICostLedger _ledger;
    private readonly ISshExecutor _ssh;
    private readonly IEnumerable<IAgentTool> _agentTools;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentLoop> _logger;

    public AgentLoop(
        AnthropicClient client,
        ICostLedger ledger,
        ISshExecutor ssh,
        IEnumerable<IAgentTool> agentTools,
        IOptions<AgentOptions> options,
        ILogger<AgentLoop> logger)
    {
        _client = client;
        _ledger = ledger;
        _ssh = ssh;
        _agentTools = agentTools;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AgentTriagedFinding>> RunAsync(
        string scanId,
        IReadOnlyList<Finding> candidates,
        CancellationToken ct)
    {
        var toolList = _agentTools.ToList();
        var toolByName = toolList.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var toolDefs = ToolDefinitions.Build(toolList);
        var systemPrompt = PromptBuilder.BuildSystemPrompt(toolList, _options.MaxIterations);
        var initialUser = PromptBuilder.BuildInitialUserMessage(scanId, candidates);

        var messages = new List<AnthropicWire.Message>
        {
            new(Role: "user", Content: new[] { new AnthropicWire.ContentBlock(Type: "text", Text: initialUser) }),
        };

        for (var iteration = 1; iteration <= _options.MaxIterations; iteration++)
        {
            // Pre-call budget check: if today's spend already exceeds the cap, bail.
            var budget = await _ledger.GetBudgetAsync(ct).ConfigureAwait(false);
            if (budget.IsExhausted)
            {
                _logger.LogWarning("Agent budget exhausted at iteration {N} (spent ${Spent:F2} / cap ${Cap:F2}); returning empty.",
                    iteration, budget.SpentUsd, budget.CapUsd);
                return Array.Empty<AgentTriagedFinding>();
            }

            var request = new AnthropicWire.MessageRequest(
                Model: _options.Model,
                MaxTokens: _options.MaxOutputTokens,
                System: systemPrompt,
                Messages: messages,
                Tools: toolDefs);

            var (response, usage) = await _client.SendAsync(request, scanId, ct).ConfigureAwait(false);
            await _ledger.RecordAsync(usage, ct).ConfigureAwait(false);

            // Append the assistant's full response to the conversation, regardless of stop_reason —
            // tool_result blocks in the next user message must reference the matching tool_use blocks.
            messages.Add(new AnthropicWire.Message("assistant", response.Content));

            if (string.Equals(response.StopReason, "end_turn", StringComparison.Ordinal))
            {
                return ParseFinalReport(response.Content);
            }

            if (!string.Equals(response.StopReason, "tool_use", StringComparison.Ordinal))
            {
                _logger.LogWarning("Unexpected stop_reason '{Reason}'; abandoning triage.", response.StopReason);
                return Array.Empty<AgentTriagedFinding>();
            }

            // Run each requested tool, gather the tool_result blocks for the next turn.
            var toolResults = new List<AnthropicWire.ContentBlock>();
            foreach (var block in response.Content)
            {
                if (!string.Equals(block.Type, "tool_use", StringComparison.Ordinal)) continue;
                if (block.Id is null || block.Name is null)
                {
                    _logger.LogWarning("Malformed tool_use block (missing id/name); skipping.");
                    continue;
                }

                var (resultJson, isError) = await ExecuteToolAsync(toolByName, block, ct).ConfigureAwait(false);
                toolResults.Add(new AnthropicWire.ContentBlock(
                    Type: "tool_result",
                    ToolUseId: block.Id,
                    Content: resultJson,
                    IsError: isError ? true : null));
            }

            if (toolResults.Count == 0)
            {
                _logger.LogWarning("Agent requested tool_use but no tool_use blocks parsed; abandoning.");
                return Array.Empty<AgentTriagedFinding>();
            }

            messages.Add(new AnthropicWire.Message("user", toolResults));
        }

        _logger.LogWarning("Agent loop hit MaxIterations ({Max}) without end_turn; returning empty.", _options.MaxIterations);
        return Array.Empty<AgentTriagedFinding>();
    }

    private async Task<(string Json, bool IsError)> ExecuteToolAsync(
        IReadOnlyDictionary<string, IAgentTool> toolByName,
        AnthropicWire.ContentBlock block,
        CancellationToken ct)
    {
        if (!toolByName.TryGetValue(block.Name!, out var tool))
        {
            _logger.LogWarning("Agent asked for unknown tool '{Tool}'.", block.Name);
            return ($"{{\"error\":\"unknown tool: {block.Name}\"}}", true);
        }

        // Anthropic delivers tool inputs as a JsonElement object. Convert each top-level
        // value to a string for our IReadOnlyDictionary<string,string> contract.
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        if (block.Input is { ValueKind: JsonValueKind.Object } inputObj)
        {
            foreach (var prop in inputObj.EnumerateObject())
            {
                args[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True or JsonValueKind.False => prop.Value.GetRawText(),
                    JsonValueKind.Null => string.Empty,
                    _ => prop.Value.GetRawText(),
                };
            }
        }

        try
        {
            var result = await tool.ExecuteAsync(_ssh, args, ct).ConfigureAwait(false);
            var formatted = PromptBuilder.FormatToolResult(result.Payload);
            return (formatted, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool {Tool} threw during agent execution.", block.Name);
            return ($"{{\"error\":\"{Escape(ex.Message)}\"}}", true);
        }
    }

    /// <summary>
    /// Parse the model's final report. We tolerate the JSON being wrapped in prose by
    /// scanning for the first <c>{</c> and last <c>}</c> in any text block.
    /// </summary>
    private IReadOnlyList<AgentTriagedFinding> ParseFinalReport(IReadOnlyList<AnthropicWire.ContentBlock> content)
    {
        foreach (var block in content)
        {
            if (!string.Equals(block.Type, "text", StringComparison.Ordinal)) continue;
            var text = block.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            var first = text.IndexOf('{');
            var last = text.LastIndexOf('}');
            if (first < 0 || last <= first) continue;
            var jsonSlice = text[first..(last + 1)];
            try
            {
                var report = JsonSerializer.Deserialize<FinalReport>(jsonSlice, ReportJsonOptions);
                if (report?.Findings is null) continue;
                return report.Findings.Select(f => new AgentTriagedFinding(
                    IdentityKey: f.IdentityKey ?? string.Empty,
                    Severity: ParseSeverity(f.Severity),
                    Summary: f.Summary ?? string.Empty,
                    Rationale: f.Rationale ?? string.Empty,
                    RecommendedAction: f.RecommendedAction ?? "none",
                    EvidenceRefs:  f.EvidenceRefs ?? [])).ToList();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse agent's final report; returning empty.");
            }
        }
        return Array.Empty<AgentTriagedFinding>();
    }

    private static Severity ParseSeverity(string? value) =>
        Enum.TryParse<Severity>(value, ignoreCase: true, out var sev) ? sev : Severity.Info;

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed record FinalReport(
        [property: JsonPropertyName("findings")] List<FinalFinding>? Findings);

    private sealed record FinalFinding(
        [property: JsonPropertyName("identity_key")] string? IdentityKey,
        [property: JsonPropertyName("severity")] string? Severity,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("rationale")] string? Rationale,
        [property: JsonPropertyName("recommended_action")] string? RecommendedAction,
        [property: JsonPropertyName("evidence_refs")] List<string>? EvidenceRefs);
}
