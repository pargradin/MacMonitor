using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace MacMonitor.Ssh;

/// <summary>
/// Loads the SSH private key from the macOS Keychain via
/// <c>/usr/bin/security find-generic-password -s &lt;name&gt; -w</c>.
///
/// The install script base64-encodes the key bytes before storing them in Keychain,
/// because <c>security</c>'s <c>-w</c> argument round-trips multi-line text unreliably
/// (bash command substitution strips trailing newlines, and the read-back path adds
/// or strips its own whitespace depending on macOS version). Base64 dodges all of
/// that: the keychain payload is a single ASCII line that we decode back to the exact
/// PEM bytes the keypair was generated with.
///
/// For backwards compatibility the loader also accepts a plain-text value (legacy
/// installs), and falls back to <see cref="SshOptions.PrivateKeyPath"/> if the keychain
/// is unavailable.
/// </summary>
public sealed class KeychainPrivateKeyProvider : IPrivateKeyProvider
{
    private const string SecurityBinary = "/usr/bin/security";
    private readonly SshOptions _options;
    private readonly ILogger<KeychainPrivateKeyProvider> _logger;

    public KeychainPrivateKeyProvider(IOptions<SshOptions> options, ILogger<KeychainPrivateKeyProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IPrivateKeySource> GetAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS() && File.Exists(SecurityBinary))
        {
            try
            {
                var raw = await ReadFromKeychainAsync(_options.KeychainItemName, ct).ConfigureAwait(false);
                var keyBytes = DecodeKeychainPayload(raw);
                using var stream = new MemoryStream(keyBytes, writable: false);
                return new PrivateKeyFile(stream);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load SSH key from Keychain item {Name}, falling back to file path.", _options.KeychainItemName);
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.PrivateKeyPath) && File.Exists(_options.PrivateKeyPath))
        {
            _logger.LogInformation("Loading SSH key from file {Path}.", _options.PrivateKeyPath);
            return new PrivateKeyFile(_options.PrivateKeyPath);
        }

        throw new InvalidOperationException(
            $"No SSH private key found. Configure either the Keychain item '{_options.KeychainItemName}' " +
            "(via scripts/install.sh) or set Ssh:PrivateKeyPath in appsettings.json.");
    }

    /// <summary>
    /// Decode the keychain payload back into raw key bytes. New installs (post-fix) store
    /// base64; older installs stored the PEM as-is. We try base64 first because it's
    /// stricter and will fail loudly on a non-base64 value, falling through to UTF-8.
    /// </summary>
    private static byte[] DecodeKeychainPayload(string payload)
    {
        // Strip any whitespace/newlines that `security` may have added. Base64 doesn't
        // care about whitespace anyway.
        var compact = StripWhitespace(payload);

        // Looks like a PEM header? Not base64 — return the original UTF-8 bytes.
        if (payload.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            return Encoding.UTF8.GetBytes(payload.Trim());
        }

        if (TryBase64Decode(compact, out var bytes))
        {
            return bytes;
        }

        // Last-ditch: treat as UTF-8 plain text.
        return Encoding.UTF8.GetBytes(payload.Trim());
    }

    private static bool TryBase64Decode(string s, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(s) || s.Length % 4 != 0)
        {
            return false;
        }
        try
        {
            bytes = Convert.FromBase64String(s);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string StripWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static async Task<string> ReadFromKeychainAsync(string serviceName, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SecurityBinary,
            ArgumentList = { "find-generic-password", "-s", serviceName, "-w" },
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
                $"`security find-generic-password -s {serviceName}` exited with code {proc.ExitCode}: {stderr.Trim()}");
        }
        // Don't .Trim() — the security binary appends a single newline that we want to keep
        // out of the payload, but if the stored value itself has trailing whitespace
        // (legacy installs), we need to preserve it for SSH.NET's PEM parser. The
        // base64/PEM detection above handles both cases.
        return stdout.TrimEnd('\r', '\n');
    }
}
