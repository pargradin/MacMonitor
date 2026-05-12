using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MacMonitor.Worker;

/// <summary>
/// BackgroundService that drives <see cref="ScanOrchestrator"/> on a fixed cadence.
/// Each tick runs a scan in its own try/catch so a single failure can't kill the loop.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly ScanOrchestrator _orchestrator;
    private readonly ScanOptions _options;
    private readonly ILogger<Worker> _logger;

    public Worker(
        ScanOrchestrator orchestrator,
        IOptions<ScanOptions> options,
        ILogger<Worker> logger)
    {
        _orchestrator = orchestrator;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MacMonitor.Worker starting. Interval: {Min} min. RunOnStartup: {Run}.",
            _options.IntervalMinutes, _options.RunOnStartup);

        if (_options.RunOnStartup)
        {
            await SafeScanAsync(stoppingToken).ConfigureAwait(false);
        }

        var period = TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes));
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SafeScanAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task SafeScanAsync(CancellationToken ct)
    {
        try
        {
            await _orchestrator.RunOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan run failed.");
        }
    }
}
