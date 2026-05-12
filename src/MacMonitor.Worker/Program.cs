using MacMonitor.Agent;
using MacMonitor.Alerts;
using MacMonitor.Core.Abstractions;
using MacMonitor.Ssh;
using MacMonitor.Storage;
using MacMonitor.Tools;
using MacMonitor.Worker;
using MacMonitor.Worker.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// ──────────────── Configuration ────────────────
// appsettings.json + environment variables. User secrets are also wired up by the
// worker SDK, useful in dev when you don't want to touch the Keychain yet.
builder.Configuration.AddEnvironmentVariables(prefix: "MACMONITOR_");

// ──────────────── Services ────────────────
builder.Services
    .AddOptions<ScanOptions>()
    .Bind(builder.Configuration.GetSection(ScanOptions.SectionName));

builder.Services
    .AddMacMonitorSsh(builder.Configuration)
    .AddMacMonitorTools()
    .AddMacMonitorAlerts(builder.Configuration)
    .AddMacMonitorStorage(builder.Configuration)
    .AddMacMonitorAgent(builder.Configuration);

// Wire the Agent's daily cost cap into the Storage cost ledger. Agent → Storage is the
// dependency direction we want; the ledger receives the value through a Func so it never
// has to reference the Agent project.
builder.Services.AddSingleton<Func<decimal>>(sp =>
    () => sp.GetRequiredService<IOptions<AgentOptions>>().Value.DailyCostCapUsd);
builder.Services.AddSingleton<ICostLedger>(sp =>
    new SqliteCostLedger(
        sp.GetRequiredService<SqliteConnectionFactory>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqliteCostLedger>>(),
        sp.GetRequiredService<Func<decimal>>()));

builder.Services.AddSingleton<ScanOrchestrator>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// Initialise the baseline DB before either path runs. Idempotent — runs migrations only
// when needed. Done eagerly so a malformed DB fails fast at startup rather than mid-scan.
using (var scope = host.Services.CreateScope())
{
    var baseline = scope.ServiceProvider.GetRequiredService<IBaselineStore>();
    await baseline.InitializeAsync(CancellationToken.None);
}

// CLI subcommand path: <verb> handles its own lifecycle and exits.
if (CliDispatcher.IsSubcommand(args))
{
    Environment.ExitCode = await CliDispatcher.DispatchAsync(args, host.Services, CancellationToken.None);
    return;
}

// Otherwise run as a long-lived worker.
await host.RunAsync();
