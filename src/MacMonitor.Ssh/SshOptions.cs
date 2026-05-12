namespace MacMonitor.Ssh;

/// <summary>
/// Options for the SSH executor. Bound from the "Ssh" section of appsettings.json.
/// Secrets (the actual private key) live in the macOS Keychain and are looked up by
/// <see cref="KeychainItemName"/>.
/// </summary>
public sealed class SshOptions
{
    public const string SectionName = "Ssh";

    /// <summary>SSH host. Default: 127.0.0.1.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>SSH port. Default: 22.</summary>
    public int Port { get; set; } = 22;

    /// <summary>Local username on the Mac. Required.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>
    /// Service name of the macOS Keychain entry holding the private key. Required.
    /// Looked up with <c>security find-generic-password -s &lt;name&gt; -w</c>.
    /// </summary>
    public string KeychainItemName { get; set; } = "MacMonitor.SshKey";

    /// <summary>
    /// Optional path to the private key on disk. Used as a fallback / for development.
    /// Ignored when running in production mode with a Keychain entry available.
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>Per-command timeout. Defaults to 10 seconds.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>SSH connect timeout. Defaults to 10 seconds.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
