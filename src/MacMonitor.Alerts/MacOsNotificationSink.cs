using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MacMonitor.Alerts;

/// <summary>
/// Fires macOS notification banners via <c>/usr/bin/osascript display notification</c>.
/// No third-party dependency. Trade-offs:
/// <list type="bullet">
///   <item>No action buttons (would require a bundled .app + UNUserNotificationCenter).</item>
///   <item>Body text is capped to ~256 chars by macOS.</item>
///   <item>On non-macOS hosts, this sink is a no-op.</item>
/// </list>
/// Throttling: per-scan-run cap and per-identity_key cooldown protect against
/// notification fatigue on noisy days. Both are process-local.
/// </summary>
public sealed class MacOsNotificationSink : IAlertSink
{
    private const string OsaScriptBinary = "/usr/bin/osascript";

    private readonly NotificationOptions _options;
    private readonly ILogger<MacOsNotificationSink> _logger;
    private readonly Severity _minSeverity;
    private readonly bool _available;

    // Per-identity cooldown: identity_key → last-fired-at. Survives scans within the same
    // process. ConcurrentDictionary because alert fan-out can theoretically race.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldown = new(StringComparer.Ordinal);

    // Per-scan emission counter. Reset whenever we observe a new scan id, which is the
    // simplest way to implement "max N per scan" without coupling to ScanOrchestrator.
    private string? _currentScanId;
    private int _currentScanEmitted;
    private readonly object _scanGate = new();

    public MacOsNotificationSink(IOptions<NotificationOptions> options, ILogger<MacOsNotificationSink> logger)
    {
        _options = options.Value;
        _logger = logger;
        _minSeverity = Enum.TryParse<Severity>(_options.MinSeverity, true, out var s) ? s : Severity.Medium;
        _available = OperatingSystem.IsMacOS() && File.Exists(OsaScriptBinary);
        if (!_available && _options.Enabled)
        {
            _logger.LogWarning("MacOsNotificationSink: osascript not available; sink will no-op.");
        }
    }

    public Task EmitAsync(Finding finding, CancellationToken ct)
    {
        if (!_options.Enabled || !_available)
        {
            return Task.CompletedTask;
        }
        if (finding.Severity < _minSeverity)
        {
            return Task.CompletedTask;
        }

        // Per-scan throttle.
        lock (_scanGate)
        {
            if (!string.Equals(_currentScanId, finding.ScanId, StringComparison.Ordinal))
            {
                _currentScanId = finding.ScanId;
                _currentScanEmitted = 0;
            }
            if (_currentScanEmitted >= Math.Max(0, _options.MaxPerScan))
            {
                return Task.CompletedTask;
            }
            _currentScanEmitted++;
        }

        // Per-identity cooldown.
        var identityKey = ExtractIdentityKey(finding);
        if (identityKey.Length > 0)
        {
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(Math.Max(0, _options.IdentityCooldownHours));
            if (_cooldown.TryGetValue(identityKey, out var lastFired) && lastFired > cutoff)
            {
                return Task.CompletedTask;
            }
            _cooldown[identityKey] = DateTimeOffset.UtcNow;
        }

        // Fire-and-forget the osascript call. We don't want notification latency to block
        // the rest of the alert sink fan-out, and we don't care about its exit code.
        _ = Task.Run(() => SendNotification(finding), CancellationToken.None);
        return Task.CompletedTask;
    }

    private void SendNotification(Finding finding)
    {
        try
        {
            var body = TruncateForBanner(finding.Summary);
            var subtitleParts = new List<string>
            {
                $"{finding.Severity} · {finding.Source}",
            };
            if (!string.IsNullOrWhiteSpace(finding.RecommendedAction)
                && !string.Equals(finding.RecommendedAction, "none", StringComparison.OrdinalIgnoreCase))
            {
                subtitleParts.Add($"Action: {TruncateForBanner(finding.RecommendedAction!, 80)}");
            }
            var subtitle = string.Join(" · ", subtitleParts);

            var script = BuildAppleScript(_options.Title, subtitle, body);

            var psi = new ProcessStartInfo
            {
                FileName = OsaScriptBinary,
                ArgumentList = { "-e", script },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                _logger.LogWarning("MacOsNotificationSink: failed to start osascript.");
                return;
            }
            // Bound the wait so a hung osascript can't accumulate.
            if (!proc.WaitForExit(5_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                _logger.LogWarning("MacOsNotificationSink: osascript timed out and was killed.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MacOsNotificationSink: notification dispatch failed.");
        }
    }

    /// <summary>
    /// Build a single-line AppleScript fragment. AppleScript string literals use double
    /// quotes; we escape internal double quotes as <c>\"</c> and backslashes as <c>\\</c>.
    /// </summary>
    internal static string BuildAppleScript(string title, string subtitle, string body)
    {
        var sb = new StringBuilder("display notification \"");
        AppendEscaped(sb, body);
        sb.Append("\" with title \"");
        AppendEscaped(sb, title);
        sb.Append("\" subtitle \"");
        AppendEscaped(sb, subtitle);
        sb.Append('"');
        return sb.ToString();
    }

    private static void AppendEscaped(StringBuilder sb, string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
    }

    private static string TruncateForBanner(string s, int max = 200)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static string ExtractIdentityKey(Finding finding)
    {
        if (finding.Evidence is null) return string.Empty;
        var prop = finding.Evidence.GetType().GetProperty("identity");
        return prop?.GetValue(finding.Evidence) as string ?? string.Empty;
    }
}
