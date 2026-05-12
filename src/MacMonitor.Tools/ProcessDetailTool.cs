using System.Diagnostics;
using System.Globalization;
using MacMonitor.Core.Abstractions;
using MacMonitor.Tools.Parsing;
using Microsoft.Extensions.Logging;

namespace MacMonitor.Tools;

public sealed class ProcessDetailTool : IAgentTool
{
    private readonly ILogger<ProcessDetailTool> _logger;

    public ProcessDetailTool(ILogger<ProcessDetailTool> logger) => _logger = logger;

    public string Name => "process_detail";

    public string Description =>
        "Given a pid, returns the process's open files, its parent-process chain up to launchd, " +
        "and the codesign signing info for its executable. Use to investigate processes flagged as suspicious.";

    public string InputJsonSchema => """
        {
          "type": "object",
          "properties": {
            "pid": { "type": "integer", "description": "The numeric pid to inspect." }
          },
          "required": ["pid"]
        }
        """;

    public async Task<ToolResult> ExecuteAsync(
        ISshExecutor ssh,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct)
    {
        if (args is null || !args.TryGetValue("pid", out var pidStr) ||
            !int.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            throw new ArgumentException("process_detail requires an integer 'pid' argument.", nameof(args));
        }

        var sw = Stopwatch.StartNew();
        var cr = await ssh.RunAsync("process-detail",
            new Dictionary<string, string> { ["pid"] = pidStr },
            ct).ConfigureAwait(false);
        var payload = ProcessDetailParser.Parse(pid, cr.StandardOutput);
        sw.Stop();
        _logger.LogInformation("process_detail({Pid}): ancestry={N}, lsofChars={C}.",
            pid, payload.Ancestry.Count, payload.LsofText.Length);
        var warnings = cr.Succeeded ? Array.Empty<string>() : new[] { $"process-detail exited {cr.ExitStatus}: {cr.StandardError.Trim()}" };
        return ToolResult.Of(Name, (object)payload, cr.StandardOutput, sw.Elapsed, warnings);
    }
}
