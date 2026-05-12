using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;
using MacMonitor.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MacMonitor.Worker;

/// <summary>
/// Phase-2 orchestrator. Per scan run:
/// <list type="number">
///   <item>Open the SSH session.</item>
///   <item>For each <see cref="ITool"/>: execute, serialize the payload, look up the previous
///     snapshot, save the current snapshot.</item>
///   <item>If no previous snapshot exists, emit a single Info baseline finding for that tool.</item>
///   <item>Otherwise compute a diff via the registered <see cref="IDiffer"/>, filter against
///     <see cref="IKnownGoodRepository"/>, and emit one finding per remaining diff item.</item>
///   <item>Persist findings to SQLite, fan out to alert sinks, mark scan complete, run retention.</item>
/// </list>
/// </summary>
public sealed class ScanOrchestrator
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ISshExecutor _ssh;
    private readonly IEnumerable<IScanTool> _tools;
    private readonly IEnumerable<IAlertSink> _sinks;
    private readonly IBaselineStore _baseline;
    private readonly IScanRepository _scans;
    private readonly IKnownGoodRepository _knownGood;
    private readonly DifferRegistry _differs;
    private readonly ITriageService _triage;
    private readonly ScanOptions _options;
    private readonly ILogger<ScanOrchestrator> _logger;

    public ScanOrchestrator(
        ISshExecutor ssh,
        IEnumerable<IScanTool> tools,
        IEnumerable<IAlertSink> sinks,
        IBaselineStore baseline,
        IScanRepository scans,
        IKnownGoodRepository knownGood,
        DifferRegistry differs,
        ITriageService triage,
        IOptions<ScanOptions> options,
        ILogger<ScanOrchestrator> logger)
    {
        _ssh = ssh;
        _tools = tools;
        _sinks = sinks;
        _baseline = baseline;
        _scans = scans;
        _knownGood = knownGood;
        _differs = differs;
        _triage = triage;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ScanResult> RunOnceAsync(CancellationToken ct)
    {
        var scanId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Scan {ScanId} starting.", scanId);

        await _scans.RecordScanStartedAsync(scanId, startedAt, ct).ConfigureAwait(false);

        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scanCts.CancelAfter(TimeSpan.FromSeconds(_options.MaxScanDurationSeconds));
        var token = scanCts.Token;

        var toolResults = new List<ToolResult>();
        var findings = new List<Finding>();
        var errors = new List<string>();

        try
        {
            await _ssh.ConnectAsync(token).ConfigureAwait(false);

            foreach (var tool in _tools)
            {
                try
                {
                    var perToolFindings = await RunOneToolAsync(scanId, tool, token).ConfigureAwait(false);
                    toolResults.Add(perToolFindings.ToolResult);
                    findings.AddRange(perToolFindings.Findings);
                    foreach (var w in perToolFindings.ToolResult.Warnings)
                    {
                        _logger.LogWarning("Tool {Tool} warning: {Warning}", tool.Name, w);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    errors.Add($"Tool {tool.Name} timed out (scan budget exhausted).");
                    _logger.LogWarning("Tool {Tool} timed out.", tool.Name);
                    break;
                }
                catch (Exception ex)
                {
                    errors.Add($"Tool {tool.Name} failed: {ex.Message}");
                    _logger.LogError(ex, "Tool {Tool} failed.", tool.Name);
                }
            }

            // Triage step. The agent runs *inside* the SSH session because its detail
            // tools call back through the same executor. Failure modes are graceful — an
            // empty result means "leave the candidates alone."
            try
            {
                var triaged = await _triage.TriageAsync(scanId, findings, token).ConfigureAwait(false);
                if (triaged.Count > 0)
                {
                    findings = MergeTriagedFindings(findings, triaged);
                    _logger.LogInformation("Triage merged {N} agent findings into candidates.", triaged.Count);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Triage failed: {ex.Message}");
                _logger.LogError(ex, "Triage failed.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"SSH session failed: {ex.Message}");
            _logger.LogError(ex, "SSH session failed.");
        }
        finally
        {
            await _ssh.DisconnectAsync().ConfigureAwait(false);
        }

        sw.Stop();
        var completedAt = startedAt + sw.Elapsed;
        var status = errors.Count == 0 ? "ok" : "partial";

        // Persist findings before fanning out so a sink crash can't lose them.
        try
        {
            await _scans.PersistFindingsAsync(findings, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errors.Add($"Persist findings failed: {ex.Message}");
            _logger.LogError(ex, "Persist findings failed.");
        }

        foreach (var finding in findings)
        {
            foreach (var sink in _sinks)
            {
                try
                {
                    await sink.EmitAsync(finding, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Alert sink {Sink} failed.", sink.GetType().Name);
                }
            }
        }

        try
        {
            await _scans.RecordScanCompletedAsync(scanId, completedAt, status, ct).ConfigureAwait(false);
            await _baseline.ApplyRetentionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-scan bookkeeping failed.");
        }

        _logger.LogInformation(
            "Scan {ScanId} complete: {Tools} tools ran, {Findings} findings, {Errors} errors, {Ms} ms.",
            scanId, toolResults.Count, findings.Count, errors.Count, sw.ElapsedMilliseconds);
        return new ScanResult(scanId, startedAt, completedAt, toolResults, findings, errors);
    }

    private async Task<(ToolResult ToolResult, IReadOnlyList<Finding> Findings)> RunOneToolAsync(
        string scanId,
        IScanTool tool,
        CancellationToken ct)
    {
        var result = await tool.ExecuteAsync(_ssh, args: null, ct).ConfigureAwait(false);
        var findings = new List<Finding>();

        // Serialize current payload (using its runtime type so the JSON has the actual fields).
        var payloadType = result.Payload.GetType();
        var currentJson = JsonSerializer.Serialize(result.Payload, payloadType, PayloadJsonOptions);
        var currentHash = Sha256Hex(currentJson);
        var itemCount = result.Payload is ICollection coll ? coll.Count : -1;

        var current = new Snapshot(
            ScanId: scanId,
            ToolName: tool.Name,
            CapturedAt: DateTimeOffset.UtcNow,
            PayloadJson: currentJson,
            PayloadHash: currentHash,
            ItemCount: itemCount);

        var previous = await _baseline.GetLatestSnapshotAsync(tool.Name, ct).ConfigureAwait(false);
        await _baseline.SaveSnapshotAsync(current, ct).ConfigureAwait(false);

        if (previous is null)
        {
            // Cold start for this tool. Emit the always-Info baseline summary first so the
            // user can see "first scan, N items" in the log, then run the heuristic over
            // every item in the snapshot and emit Added-style findings for anything
            // suspicious. Those flow through the normal triage path — meaning the agent
            // gets to investigate ~5–20 items per cold-start scan instead of 0 (which was
            // the old behavior) or 500+ (which would blow the cost cap).
            findings.Add(FindingBuilder.Baseline(scanId, tool.Name, itemCount));
            var suspicious = BaselineHeuristics.FindSuspicious(tool.Name, result.Payload).ToList();
            if (suspicious.Count > 0)
            {
                _logger.LogInformation(
                    "Tool {Tool}: cold-start heuristic flagged {N} item(s) for triage.",
                    tool.Name, suspicious.Count);
                foreach (var item in suspicious)
                {
                    findings.Add(FindingBuilder.Added(scanId, tool.Name, item));
                }
            }
            return (result, findings);
        }

        if (string.Equals(previous.PayloadHash, currentHash, StringComparison.Ordinal))
        {
            // No-op scan for this tool: skip diff entirely.
            return (result, findings);
        }

        if (!_differs.TryGet(tool.Name, out var differ))
        {
            _logger.LogWarning("No differ registered for tool {Tool}; emitting summary only.", tool.Name);
            findings.Add(FindingBuilder.Baseline(scanId, tool.Name, itemCount));
            return (result, findings);
        }

        var diff = differ.Compute(previous.PayloadJson, currentJson);
        if (diff.IsEmpty)
        {
            return (result, findings);
        }

        // Bulk allow-list lookup for every identity touched in this diff.
        var keys = diff.Added.Select(d => d.IdentityKey)
            .Concat(diff.Removed.Select(d => d.IdentityKey))
            .Concat(diff.Changed.Select(d => d.IdentityKey))
            .ToHashSet(StringComparer.Ordinal);
        var allowed = await _knownGood.FilterAllowedAsync(tool.Name, keys, ct).ConfigureAwait(false);

        foreach (var item in diff.Added)
        {
            if (allowed.Contains(item.IdentityKey)) { continue; }
            findings.Add(FindingBuilder.Added(scanId, tool.Name, item));
        }
        foreach (var item in diff.Removed)
        {
            if (allowed.Contains(item.IdentityKey)) { continue; }
            findings.Add(FindingBuilder.Removed(scanId, tool.Name, item));
        }
        foreach (var change in diff.Changed)
        {
            if (allowed.Contains(change.IdentityKey)) { continue; }
            findings.Add(FindingBuilder.Changed(scanId, tool.Name, change));
        }

        _logger.LogInformation(
            "Tool {Tool}: diff +{Added} -{Removed} ~{Changed} → {Emitted} finding(s) after allow-list.",
            tool.Name, diff.Added.Count, diff.Removed.Count, diff.Changed.Count, findings.Count);

        return (result, findings);
    }

    private static string Sha256Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Merge agent-triaged findings back into the candidate list by identity_key. For each
    /// candidate that has a matching triaged entry: replace severity/summary, fill rationale
    /// and recommended_action. Candidates without a matching triage stay as-is.
    /// </summary>
    private static List<Finding> MergeTriagedFindings(
        List<Finding> candidates,
        IReadOnlyList<AgentTriagedFinding> triaged)
    {
        // Index by identity_key for O(1) lookup. If duplicate keys appear, last one wins.
        var byKey = new Dictionary<string, AgentTriagedFinding>(StringComparer.Ordinal);
        foreach (var t in triaged)
        {
            if (!string.IsNullOrEmpty(t.IdentityKey))
            {
                byKey[t.IdentityKey] = t;
            }
        }

        var merged = new List<Finding>(candidates.Count);
        foreach (var f in candidates)
        {
            var key = ExtractIdentityKey(f.Evidence);
            if (key.Length > 0 && byKey.TryGetValue(key, out var t))
            {
                merged.Add(f with
                {
                    Severity = t.Severity,
                    Summary = t.Summary.Length > 0 ? t.Summary : f.Summary,
                    Rationale = t.Rationale,
                    RecommendedAction = t.RecommendedAction,
                });
            }
            else
            {
                merged.Add(f);
            }
        }
        return merged;
    }

    /// <summary>
    /// Same shape as <c>PromptBuilder.ExtractIdentityKey</c> — pulls the <c>identity</c>
    /// property off the anonymous evidence object the FindingBuilder emits for diff items.
    /// Baseline findings have no identity property; they return empty.
    /// </summary>
    private static string ExtractIdentityKey(object? evidence)
    {
        if (evidence is null) return string.Empty;
        var prop = evidence.GetType().GetProperty("identity");
        return prop?.GetValue(evidence) as string ?? string.Empty;
    }
}
