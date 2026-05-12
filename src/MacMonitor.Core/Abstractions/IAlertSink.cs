using MacMonitor.Core.Models;

namespace MacMonitor.Core.Abstractions;

/// <summary>
/// Destination for findings emitted by a scan run. A scan may fan out to multiple sinks
/// (file, macOS notification, webhook, …); each sink should be independently fail-safe.
/// </summary>
public interface IAlertSink
{
    Task EmitAsync(Finding finding, CancellationToken ct);
}
