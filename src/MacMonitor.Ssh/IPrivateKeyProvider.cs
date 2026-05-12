using Renci.SshNet;

namespace MacMonitor.Ssh;

/// <summary>
/// Returns a Renci.SshNet <see cref="IPrivateKeySource"/> built from a key stored
/// outside the application — typically the macOS Keychain. Implementations should
/// avoid leaving the key material on disk.
/// </summary>
public interface IPrivateKeyProvider
{
    Task<IPrivateKeySource> GetAsync(CancellationToken ct);
}
