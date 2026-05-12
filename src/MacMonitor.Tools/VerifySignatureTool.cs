using System.Diagnostics;
using MacMonitor.Core.Abstractions;
using MacMonitor.Tools.Parsing;
using Microsoft.Extensions.Logging;

namespace MacMonitor.Tools;

public sealed class VerifySignatureTool : IAgentTool
{
    private readonly ILogger<VerifySignatureTool> _logger;

    public VerifySignatureTool(ILogger<VerifySignatureTool> logger) => _logger = logger;

    public string Name => "verify_signature";

    public string Description =>
        "Given a path to a binary or .app bundle, returns codesign verification status, " +
        "signing identifier, team id, and the spctl Gatekeeper assessment. Use to decide " +
        "whether a flagged executable is from a known publisher.";

    public string InputJsonSchema => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Absolute path to the executable, dylib, or .app bundle." }
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
            throw new ArgumentException("verify_signature requires a non-empty 'path' argument.", nameof(args));
        }
        var sw = Stopwatch.StartNew();
        var cr = await ssh.RunAsync("verify-signature",
            new Dictionary<string, string> { ["path"] = path },
            ct).ConfigureAwait(false);
        var payload = SignatureParser.Parse(path, cr.StandardOutput + "\n" + cr.StandardError);
        sw.Stop();
        _logger.LogInformation("verify_signature({Path}): verified={V}, accepted={A}.",
            path, payload.Verified, payload.Accepted);
        // codesign exits non-zero on unsigned binaries — that's not a tool error, that's a finding.
        return ToolResult.Of(Name, (object)payload, cr.StandardOutput, sw.Elapsed, Array.Empty<string>());
    }
}
