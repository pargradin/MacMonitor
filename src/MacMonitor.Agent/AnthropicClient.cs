using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MacMonitor.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MacMonitor.Agent;

/// <summary>
/// Raw <see cref="HttpClient"/> wrapper around Anthropic's <c>POST /v1/messages</c>.
/// One method, one retry policy, no SDK indirection.
/// </summary>
public sealed class AnthropicClient
{
    private readonly HttpClient _http;
    private readonly IKeychainSecretProvider _secrets;
    private readonly AgentOptions _options;
    private readonly ILogger<AnthropicClient> _logger;

    public AnthropicClient(
        HttpClient http,
        IKeychainSecretProvider secrets,
        IOptions<AgentOptions> options,
        ILogger<AnthropicClient> logger)
    {
        _http = http;
        _secrets = secrets;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(AnthropicWire.MessageResponse Response, AgentUsage Usage)> SendAsync(
        AnthropicWire.MessageRequest request,
        string? scanId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var apiKey = await _secrets.GetSecretAsync(_options.AnthropicKeychainItem, ct).ConfigureAwait(false);

        // Up to 3 attempts on 429 / 5xx with linear-ish backoff. Keep it simple — the loop
        // above already bounds the total wall clock.
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            using var http = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
            http.Headers.Add("x-api-key", apiKey);
            // anthropic-version was set as a default header on the HttpClient by DI.
            http.Content = JsonContent.Create(request, options: AnthropicWire.JsonOptions);

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(http, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                _logger.LogWarning("Anthropic call failed (attempt {N}/{Max}); backing off.", attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct).ConfigureAwait(false);
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500)
            {
                if (attempt < maxAttempts)
                {
                    var retryAfter = ParseRetryAfter(resp.Headers.RetryAfter) ?? TimeSpan.FromSeconds(attempt);
                    _logger.LogWarning("Anthropic returned {Status}; retrying after {Sec}s.", (int)resp.StatusCode, retryAfter.TotalSeconds);
                    resp.Dispose();
                    await Task.Delay(retryAfter, ct).ConfigureAwait(false);
                    continue;
                }
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Anthropic request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {Truncate(body, 1024)}");
                }

                var parsed = await resp.Content.ReadFromJsonAsync<AnthropicWire.MessageResponse>(
                    AnthropicWire.JsonOptions, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Anthropic returned empty body.");

                var cost = ModelPricing.Compute(
                    parsed.Model,
                    parsed.Usage.InputTokens,
                    parsed.Usage.OutputTokens,
                    parsed.Usage.CacheReadInputTokens ?? 0,
                    parsed.Usage.CacheCreationInputTokens ?? 0);
                var usage = new AgentUsage(
                    OccurredAt: DateTimeOffset.UtcNow,
                    ScanId: scanId,
                    Model: parsed.Model,
                    InputTokens: parsed.Usage.InputTokens,
                    OutputTokens: parsed.Usage.OutputTokens,
                    CacheReadInputTokens: parsed.Usage.CacheReadInputTokens ?? 0,
                    CacheCreationInputTokens: parsed.Usage.CacheCreationInputTokens ?? 0,
                    CostUsd: cost);
                return (parsed, usage);
            }
        }
    }

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } dt) return dt - DateTimeOffset.UtcNow;
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
