using System.Diagnostics;
using MacMonitor.Core.Abstractions;
using MacMonitor.Tools.Parsing;
using Microsoft.Extensions.Logging;

namespace MacMonitor.Tools;

public sealed class NetworkConnectionsTool : IScanTool
{
    private readonly ILogger<NetworkConnectionsTool> _logger;

    public NetworkConnectionsTool(ILogger<NetworkConnectionsTool> logger) => _logger = logger;

    public string Name => "network_connections";

    public string Description =>
        "Returns all active TCP/UDP endpoints (listening or established) with their owning process. " +
        "Source: lsof -nP -iTCP -iUDP -F pcPnT.";

    public async Task<ToolResult> ExecuteAsync(
        ISshExecutor ssh,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var cr = await ssh.RunAsync("network-connections", null, ct).ConfigureAwait(false);
        var conns = LsofParser.Parse(cr.StandardOutput);
        sw.Stop();
        _logger.LogInformation("network_connections: parsed {Count} endpoints in {Ms} ms.", conns.Count, sw.ElapsedMilliseconds);
        // lsof returns non-zero if some pids it tried to inspect went away mid-run; that's noisy but not fatal.
        var warnings = cr.Succeeded ? Array.Empty<string>() : new[] { $"lsof exited {cr.ExitStatus} (often benign): {cr.StandardError.Trim()}" };
        return ToolResult.Of(Name, (object)conns, cr.StandardOutput, sw.Elapsed, warnings);
    }
}
