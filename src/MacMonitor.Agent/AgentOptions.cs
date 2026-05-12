namespace MacMonitor.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>If false, the orchestrator skips triage and emits raw Phase-2 findings.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Anthropic model id. Default: claude-haiku-4-5 (cheap, fast, good for triage).
    /// Switch to claude-sonnet-4-6 if you want stronger judgement on edge cases.
    /// </summary>
    public string Model { get; set; } = "claude-haiku-4-5-20251001";

    /// <summary>Keychain item that stores the Anthropic API key.</summary>
    public string AnthropicKeychainItem { get; set; } = "MacMonitor.AnthropicKey";

    /// <summary>API base URL. Override for staging or proxy use.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>Anthropic API version header.</summary>
    public string ApiVersion { get; set; } = "2023-06-01";

    /// <summary>Hard ceiling on the tool-use loop. Default: 8.</summary>
    public int MaxIterations { get; set; } = 8;

    /// <summary>Estimated input-token cap per single Messages call.</summary>
    public int MaxInputTokensPerCall { get; set; } = 40_000;

    /// <summary>Output-token cap per single Messages call.</summary>
    public int MaxOutputTokens { get; set; } = 4_096;

    /// <summary>Wall-clock for the whole triage operation.</summary>
    public int WallClockBudgetSeconds { get; set; } = 60;

    /// <summary>Soft daily cost cap in USD. Once exceeded, triage is paused for the day.</summary>
    public decimal DailyCostCapUsd { get; set; } = 5.00m;

    /// <summary>
    /// If true, the orchestrator emits both raw Phase-2 findings and triaged findings —
    /// useful for debugging the agent's behavior; default off in production.
    /// </summary>
    public bool EmitRawFindings { get; set; } = false;
}
