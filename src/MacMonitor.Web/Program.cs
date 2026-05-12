using MacMonitor.Agent;
using MacMonitor.Alerts;
using MacMonitor.Core.Abstractions;
using MacMonitor.Ssh;
using MacMonitor.Storage;
using MacMonitor.Tools;
using MacMonitor.Web;
using MacMonitor.Web.Components;
using MacMonitor.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Load the static-web-assets manifest unconditionally. By default ASP.NET Core only
// loads it when `IWebHostEnvironment.IsDevelopment()` is true, which means running with
// `dotnet run` (env defaults to Production) leaves `_framework/blazor.web.js` and the
// rest of the package-contributed assets unfound. This call is idempotent — Development
// environments already have it on. See: warning "Static Web Assets are not enabled."
builder.WebHost.UseStaticWebAssets();

// ──────────────── Configuration ────────────────
builder.Configuration.AddEnvironmentVariables(prefix: "MACMONITOR_");
builder.Services.AddOptions<WebOptions>()
    .Bind(builder.Configuration.GetSection(WebOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddOptions<ScanOptions>()
    .Bind(builder.Configuration.GetSection(ScanOptions.SectionName));

// ──────────────── MacMonitor runtime ────────────────
// Same DI calls the Worker project uses. NOTE: this duplication is acknowledged in
// PHASE4.md — a follow-up refactor will extract these into a shared
// `services.AddMacMonitorRuntime(config)` helper.
builder.Services
    .AddMacMonitorSsh(builder.Configuration)
    .AddMacMonitorTools()
    .AddMacMonitorAlerts(builder.Configuration)
    .AddMacMonitorStorage(builder.Configuration)
    .AddMacMonitorAgent(builder.Configuration);

builder.Services.AddSingleton<Func<decimal>>(sp =>
    () => sp.GetRequiredService<IOptions<AgentOptions>>().Value.DailyCostCapUsd);
builder.Services.AddSingleton<ICostLedger>(sp =>
    new SqliteCostLedger(
        sp.GetRequiredService<SqliteConnectionFactory>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqliteCostLedger>>(),
        sp.GetRequiredService<Func<decimal>>()));

builder.Services.AddSingleton<ScanOrchestrator>();

// ──────────────── Blazor Server ────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Bind Kestrel to the configured loopback address only.
var webOpts = builder.Configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(System.Net.IPAddress.Parse(webOpts.BindAddress), webOpts.Port);
});

var app = builder.Build();

// Initialise the baseline DB before serving any pages — same pattern as the worker.
using (var scope = app.Services.CreateScope())
{
    var baseline = scope.ServiceProvider.GetRequiredService<IBaselineStore>();
    await baseline.InitializeAsync(CancellationToken.None);
}

// MapStaticAssets serves both the project's own wwwroot/ AND the Blazor framework
// JavaScript (`_framework/blazor.web.js`) that ships as a static web asset inside the
// Microsoft.AspNetCore.Components.Server package. The older UseStaticFiles() only
// covers the project's wwwroot/, which is why a fresh Blazor Web App (net9+) returns
// 404 for `_framework/blazor.web.js` if you only call UseStaticFiles().
app.MapStaticAssets();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
