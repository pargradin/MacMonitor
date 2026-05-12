using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MacMonitor.Agent;

/// <summary>
/// String-valued counterpart to the SSH project's <c>KeychainPrivateKeyProvider</c>: reads
/// a generic-password from the macOS Keychain by service name. Used for the Anthropic API
/// key. Resolved values are cached for the process lifetime so we don't spawn
/// <c>/usr/bin/security</c> on every API call.
/// </summary>
public interface IKeychainSecretProvider
{
    Task<string> GetSecretAsync(string itemName, CancellationToken ct);
}

public sealed class KeychainSecretProvider : IKeychainSecretProvider
{
    private const string SecurityBinary = "/usr/bin/security";

    private readonly ILogger<KeychainSecretProvider> _logger;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KeychainSecretProvider(ILogger<KeychainSecretProvider> logger)
    {
        _logger = logger;
    }

    public async Task<string> GetSecretAsync(string itemName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemName);
        if (_cache.TryGetValue(itemName, out var cached))
        {
            return cached;
        }

        if (!OperatingSystem.IsMacOS() || !File.Exists(SecurityBinary))
        {
            throw new InvalidOperationException(
                "macOS Keychain not available; this build path requires /usr/bin/security.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(itemName, out cached))
            {
                return cached;
            }
            var value = await ReadFromKeychainAsync(itemName, ct).ConfigureAwait(false);
            _cache[itemName] = value;
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> ReadFromKeychainAsync(string itemName, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SecurityBinary,
            ArgumentList = { "find-generic-password", "-s", itemName, "-w" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start /usr/bin/security.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"`security find-generic-password -s {itemName}` exited {proc.ExitCode}: {stderr.Trim()}");
        }
        return stdout.TrimEnd('\r', '\n');
    }
}
