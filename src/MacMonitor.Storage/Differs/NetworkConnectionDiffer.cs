using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;

namespace MacMonitor.Storage.Differs;

/// <summary>
/// Diff strategy for <see cref="NetworkConnection"/>.
///
/// <list type="bullet">
///   <item><b>IdentityKey</b> = <c>process|protocol|remote</c>, where remote falls back to
///     <c>LISTEN:&lt;localPort&gt;</c> for listening sockets. Ephemeral local ports are
///     deliberately excluded from the key — every TCP client connection picks a fresh local
///     port, and including it would make every connection look "new" on every scan.</item>
///   <item><b>ContentHash</b> = empty. State (ESTABLISHED → CLOSE_WAIT → …) churn is noise.</item>
///   <item><b>Policy</b> = Added only. New outbound endpoints / new listeners are the signal.</item>
/// </list>
/// </summary>
public sealed class NetworkConnectionDiffer : DifferBase<NetworkConnection>
{
    public override string ToolName => "network_connections";

    public override DiffEmissionPolicy Policy => DiffEmissionPolicy.Added;

    public override string IdentityKey(NetworkConnection item)
    {
        if (string.IsNullOrEmpty(item.RemoteAddress))
        {
            // Listening socket — key by the local port (last component after the final ':').
            var idx = item.LocalAddress.LastIndexOf(':');
            var port = idx >= 0 ? item.LocalAddress[(idx + 1)..] : item.LocalAddress;
            return $"{item.ProcessName}|{item.Protocol}|LISTEN:{port}";
        }
        return $"{item.ProcessName}|{item.Protocol}|{item.RemoteAddress}";
    }

    public override string ContentHash(NetworkConnection item) => string.Empty;
}
