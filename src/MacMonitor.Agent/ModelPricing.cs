using System.Collections.Frozen;

namespace MacMonitor.Agent;

/// <summary>
/// Hard-coded Anthropic pricing in USD per million tokens. Verify against current pricing
/// at <c>https://www.anthropic.com/pricing</c> in the implementation round before relying
/// on this for cost-cap enforcement.
/// </summary>
public static class ModelPricing
{
    public sealed record Tier(decimal InputPerM, decimal OutputPerM, decimal CacheReadPerM, decimal CacheWritePerM);

    public static readonly FrozenDictionary<string, Tier> ByModel = new Dictionary<string, Tier>(StringComparer.Ordinal)
    {
        ["claude-haiku-4-5-20251001"] = new(InputPerM: 1.00m, OutputPerM: 5.00m, CacheReadPerM: 0.10m, CacheWritePerM: 1.25m),
        ["claude-sonnet-4-6"] = new(InputPerM: 3.00m, OutputPerM: 15.00m, CacheReadPerM: 0.30m, CacheWritePerM: 3.75m),
        ["claude-opus-4-6"] = new(InputPerM: 15.00m, OutputPerM: 75.00m, CacheReadPerM: 1.50m, CacheWritePerM: 18.75m),
    }.ToFrozenDictionary();

    /// <summary>
    /// Compute the dollar cost for one usage record given the model. Falls back to a
    /// conservative high estimate (Sonnet pricing) when the model isn't in the table —
    /// safer than billing zero for a model we forgot to register.
    /// </summary>
    public static decimal Compute(string model, int inputTokens, int outputTokens, int cacheReadTokens, int cacheWriteTokens)
    {
        var tier = ByModel.TryGetValue(model, out var t) ? t : ByModel["claude-sonnet-4-6"];
        return (inputTokens * tier.InputPerM
              + outputTokens * tier.OutputPerM
              + cacheReadTokens * tier.CacheReadPerM
              + cacheWriteTokens * tier.CacheWritePerM) / 1_000_000m;
    }
}
