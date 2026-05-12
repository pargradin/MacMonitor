using System.Diagnostics;
using MacMonitor.Core.Abstractions;
using MacMonitor.Tools.Parsing;
using Microsoft.Extensions.Logging;

namespace MacMonitor.Tools;

public sealed class ReadLaunchPlistTool : IAgentTool
{
    private readonly ILogger<ReadLaunchPlistTool> _logger;

    public ReadLaunchPlistTool(ILogger<ReadLaunchPlistTool> logger) => _logger = logger;

    public string Name => "read_launch_plist";

    public string Description =>
        "Given the path to a launchd plist, returns its parsed contents. Use to inspect a " +
        "newly-discovered persistence entry's Label, ProgramArguments, run conditions, and user.";

    public string InputJsonSchema => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Absolute path to the .plist file under one of the four launchd scopes." }
          },
          "required": ["path"]
        }
        """;

    public async Task<ToolResult> ExecuteAsync(
        ISshExecutor ssh,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct)
    {
        if (args is null || !args.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("read_launch_plist requires a non-empty 'path' argument.", nameof(args));
        }
        var sw = Stopwatch.StartNew();
        var cr = await ssh.RunAsync("read-launch-plist",
            new Dictionary<string, string> { ["path"] = path },
            ct).ConfigureAwait(false);
        var payload = PlistParser.Parse(path, cr.StandardOutput);
        sw.Stop();
        _logger.LogInformation("read_launch_plist({Path}): label={Label}, argc={Argc}.",
            path, payload.Label ?? "?", payload.ProgramArguments.Count);
        var warnings = cr.Succeeded ? Array.Empty<string>() : new[] { $"plutil exited {cr.ExitStatus}: {cr.StandardError.Trim()}" };
        return ToolResult.Of(Name, (object)payload, cr.StandardOutput, sw.Elapsed, warnings);
    }
}
