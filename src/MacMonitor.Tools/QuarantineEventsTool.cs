using System.Diagnostics;
using MacMonitor.Core.Abstractions;
using MacMonitor.Tools.Parsing;
using Microsoft.Extensions.Logging;

namespace MacMonitor.Tools;

public sealed class QuarantineEventsTool : IAgentTool
{
    private readonly ILogger<QuarantineEventsTool> _logger;

    public QuarantineEventsTool(ILogger<QuarantineEventsTool> logger) => _logger = logger;

    public string Name => "quarantine_events";

    public string Description =>
        "Returns the last 50 LaunchServices quarantine events (timestamp, originating " +
        "agent / app, source URL, data URL). Use to attribute a flagged download to its " +
        "browser and origin.";

    public string InputJsonSchema => """
        {
          "type": "object",
          "properties": {},
          "required": []
        }
        """;

    public async Task<ToolResult> ExecuteAsync(
        ISshExecutor ssh,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var cr = await ssh.RunAsync("quarantine-events", null, ct).ConfigureAwait(false);
        var payload = QuarantineParser.Parse(cr.StandardOutput);
        sw.Stop();
        _logger.LogInformation("quarantine_events: parsed {N} events.", payload.Events.Count);
        var warnings = cr.Succeeded ? Array.Empty<string>() : new[] { $"sqlite3 exited {cr.ExitStatus}: {cr.StandardError.Trim()}" };
        return ToolResult.Of(Name, (object)payload, cr.StandardOutput, sw.Elapsed, warnings);
    }
}
