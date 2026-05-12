using System.Diagnostics;
using MacMonitor.Core.Abstractions;
using MacMonitor.Tools.Parsing;
using Microsoft.Extensions.Logging;

namespace MacMonitor.Tools;

public sealed class ListProcessesTool : IScanTool
{
    private readonly ILogger<ListProcessesTool> _logger;

    public ListProcessesTool(ILogger<ListProcessesTool> logger) => _logger = logger;

    public string Name => "list_processes";

    public string Description =>
        "Returns the current process table with pid, ppid, user, %cpu, %mem and the full command line.";

    public async Task<ToolResult> ExecuteAsync(
        ISshExecutor ssh,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var cr = await ssh.RunAsync("list-processes", null, ct).ConfigureAwait(false);
        var processes = PsParser.Parse(cr.StandardOutput);
        sw.Stop();
        _logger.LogInformation("list_processes: parsed {Count} processes in {Ms} ms.", processes.Count, sw.ElapsedMilliseconds);
        var warnings = cr.Succeeded ? Array.Empty<string>() : new[] { $"ps exited {cr.ExitStatus}: {cr.StandardError.Trim()}" };
        return ToolResult.Of(Name, (object)processes, cr.StandardOutput, sw.Elapsed, warnings);
    }
}
