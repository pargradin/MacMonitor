using System.Diagnostics;
using MacMonitor.Core.Abstractions;
using MacMonitor.Tools.Parsing;
using Microsoft.Extensions.Logging;

namespace MacMonitor.Tools;

public sealed class ListLaunchAgentsTool : IScanTool
{
    private readonly ILogger<ListLaunchAgentsTool> _logger;

    public ListLaunchAgentsTool(ILogger<ListLaunchAgentsTool> logger) => _logger = logger;

    public string Name => "list_launch_agents";

    public string Description =>
        "Lists every .plist under the four standard launchd scopes: " +
        "/Library/LaunchAgents, ~/Library/LaunchAgents, /Library/LaunchDaemons, /System/Library/LaunchDaemons. " +
        "Returns path, scope, modified-at and size for each.";

    public async Task<ToolResult> ExecuteAsync(
        ISshExecutor ssh,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var cr = await ssh.RunAsync("list-launch-items", null, ct).ConfigureAwait(false);
        var home = Environment.GetEnvironmentVariable("HOME") ?? "/Users";
        var items = LaunchItemParser.Parse(cr.StandardOutput, home);
        sw.Stop();
        _logger.LogInformation("list_launch_agents: parsed {Count} plists in {Ms} ms.", items.Count, sw.ElapsedMilliseconds);
        var warnings = cr.Succeeded ? Array.Empty<string>() : new[] { $"find/stat exited {cr.ExitStatus}: {cr.StandardError.Trim()}" };
        return ToolResult.Of(Name, (object)items, cr.StandardOutput, sw.Elapsed, warnings);
    }
}
