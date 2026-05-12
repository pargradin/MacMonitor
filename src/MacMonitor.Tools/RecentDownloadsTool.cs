using System.Diagnostics;
using MacMonitor.Core.Abstractions;
using MacMonitor.Tools.Parsing;
using Microsoft.Extensions.Logging;

namespace MacMonitor.Tools;

public sealed class RecentDownloadsTool : IScanTool
{
    private readonly ILogger<RecentDownloadsTool> _logger;

    public RecentDownloadsTool(ILogger<RecentDownloadsTool> logger) => _logger = logger;

    public string Name => "recent_downloads";

    public string Description =>
        "Files in ~/Downloads modified in the last 30 days, with size, owner and the " +
        "com.apple.quarantine extended attribute (where present).";

    public async Task<ToolResult> ExecuteAsync(
        ISshExecutor ssh,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var cr = await ssh.RunAsync("recent-downloads", null, ct).ConfigureAwait(false);
        var files = DownloadsParser.Parse(cr.StandardOutput);
        sw.Stop();
        _logger.LogInformation("recent_downloads: parsed {Count} files in {Ms} ms.", files.Count, sw.ElapsedMilliseconds);
        // Empty Downloads folder or missing FDA both produce empty stdout — surface a hint either way.
        var warnings = new List<string>();
        if (!cr.Succeeded)
        {
            warnings.Add($"find/stat exited {cr.ExitStatus}: {cr.StandardError.Trim()}");
        }
        if (files.Count == 0)
        {
            warnings.Add("No files returned. If you expected some, verify Full Disk Access is granted to sshd-keygen-wrapper.");
        }
        return ToolResult.Of(Name, (object)files, cr.StandardOutput, sw.Elapsed, warnings);
    }
}
