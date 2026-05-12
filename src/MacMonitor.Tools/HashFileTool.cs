using System.Diagnostics;
using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace MacMonitor.Tools;

public sealed class HashFileTool : IAgentTool
{
    private readonly ILogger<HashFileTool> _logger;

    public HashFileTool(ILogger<HashFileTool> logger) => _logger = logger;

    public string Name => "hash_file";

    public string Description =>
        "Returns the SHA-256 hash of the file at the given path. Use when you want to " +
        "include the hash in your rationale or when the same file's identity needs to " +
        "be confirmed across snapshots.";

    public string InputJsonSchema => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Absolute path to a regular file." }
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
            throw new ArgumentException("hash_file requires a non-empty 'path' argument.", nameof(args));
        }
        var sw = Stopwatch.StartNew();
        var cr = await ssh.RunAsync("hash-file",
            new Dictionary<string, string> { ["path"] = path },
            ct).ConfigureAwait(false);
        sw.Stop();

        // shasum output: "<sha256-hex>  /absolute/path"
        var sha = string.Empty;
        var line = cr.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (!string.IsNullOrEmpty(line))
        {
            var space = line.IndexOf(' ');
            sha = space > 0 ? line[..space] : line;
        }

        var payload = new HashPayload(path, sha);
        _logger.LogInformation("hash_file({Path}): sha={ShaFirst8}…", path,
            sha.Length >= 8 ? sha[..8] : sha);
        var warnings = cr.Succeeded
            ? (string.IsNullOrEmpty(sha) ? new[] { "shasum produced no output (file may not exist or be readable)." } : Array.Empty<string>())
            : new[] { $"shasum exited {cr.ExitStatus}: {cr.StandardError.Trim()}" };
        return ToolResult.Of(Name, (object)payload, cr.StandardOutput, sw.Elapsed, warnings);
    }
}
