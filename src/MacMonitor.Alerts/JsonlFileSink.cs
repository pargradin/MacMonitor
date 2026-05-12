using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MacMonitor.Alerts;

/// <summary>
/// Append-only JSONL sink. One file per UTC day. Designed to be tail-friendly
/// (each line is a complete, self-describing finding) and crash-resilient (we open,
/// append, fsync, close on every write — fine at Phase-1 cadence).
/// </summary>
public sealed class JsonlFileSink : IAlertSink
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AlertOptions _options;
    private readonly ILogger<JsonlFileSink> _logger;
    private readonly Severity _minSeverity;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonlFileSink(IOptions<AlertOptions> options, ILogger<JsonlFileSink> logger)
    {
        _options = options.Value;
        _logger = logger;
        _minSeverity = Enum.TryParse<Severity>(_options.MinSeverity, true, out var s) ? s : Severity.Info;
    }

    public async Task EmitAsync(Finding finding, CancellationToken ct)
    {
        if (finding.Severity < _minSeverity)
        {
            return;
        }

        var path = ResolvePath(finding.CreatedAt);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var line = JsonSerializer.Serialize(finding, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var fs = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            await fs.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append finding to {Path}.", path);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private string ResolvePath(DateTimeOffset createdAt)
    {
        var dir = ExpandTilde(_options.LogDirectory);
        var name = $"{_options.FilePrefix}-{createdAt.UtcDateTime:yyyy-MM-dd}.jsonl";
        return Path.Combine(dir, name);
    }

    private static string ExpandTilde(string path)
    {
        if (path.StartsWith('~'))
        {
            var home = Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path.TrimStart('~').TrimStart('/'));
        }
        return path;
    }
}
