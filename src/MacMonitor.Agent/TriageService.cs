using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MacMonitor.Agent;

/// <summary>
/// Orchestrator-facing facade. All failure modes (disabled, no candidates, budget
/// exhausted, exception) return an empty list so the orchestrator can fall back to the
/// raw Phase-2 candidates without special-casing each one.
/// </summary>
public sealed class TriageService : ITriageService
{
    private readonly AgentLoop _loop;
    private readonly ICostLedger _ledger;
    private readonly AgentOptions _options;
    private readonly ILogger<TriageService> _logger;

    public TriageService(
        AgentLoop loop,
        ICostLedger ledger,
        IOptions<AgentOptions> options,
        ILogger<TriageService> logger)
    {
        _loop = loop;
        _ledger = ledger;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AgentTriagedFinding>> TriageAsync(
        string scanId,
        IReadOnlyList<Finding> candidates,
        CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return Array.Empty<AgentTriagedFinding>();
        }
        if (candidates.Count == 0)
        {
            return Array.Empty<AgentTriagedFinding>();
        }

        var budget = await _ledger.GetBudgetAsync(ct).ConfigureAwait(false);
        if (budget.IsExhausted)
        {
            _logger.LogWarning("Triage skipped: daily cap reached (${Spent:F2} / ${Cap:F2}).",
                budget.SpentUsd, budget.CapUsd);
            return Array.Empty<AgentTriagedFinding>();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.WallClockBudgetSeconds));

        try
        {
            return await _loop.RunAsync(scanId, candidates, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Triage timed out after {Sec}s; returning empty.", _options.WallClockBudgetSeconds);
            return Array.Empty<AgentTriagedFinding>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Triage failed; falling back to raw findings.");
            return Array.Empty<AgentTriagedFinding>();
        }
    }
}
