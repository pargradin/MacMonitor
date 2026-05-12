using System.Text.Json;
using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace MacMonitor.Worker.Cli;

/// <summary>
/// Tiny argv dispatcher. We deliberately don't bring in System.CommandLine — five subcommands
/// don't justify a 200KB dependency, and the parsing here is shallow enough that a switch
/// is more readable than a fluent builder.
///
/// Subcommands:
/// <list type="bullet">
///   <item><c>once</c> — run a single scan and exit (Phase-1 smoke-test path).</item>
///   <item><c>allow &lt;tool&gt; &lt;identity_key&gt; [note]</c> — add a known-good entry.</item>
///   <item><c>deny &lt;tool&gt; &lt;identity_key&gt;</c> — remove a known-good entry.</item>
///   <item><c>list-allow [tool]</c> — list known-good entries (optionally for one tool).</item>
///   <item><c>findings [limit] [min-severity]</c> — print recent findings as JSONL.</item>
/// </list>
/// </summary>
internal static class CliDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static bool IsSubcommand(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }
        return args[0] is "once" or "allow" or "deny" or "list-allow" or "findings" or "cost" or "help" or "--help" or "-h";
    }

    public static async Task<int> DispatchAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var verb = args[0];
        try
        {
            return verb switch
            {
                "once" => await RunOnceAsync(services, ct).ConfigureAwait(false),
                "allow" => await AllowAsync(args, services, ct).ConfigureAwait(false),
                "deny" => await DenyAsync(args, services, ct).ConfigureAwait(false),
                "list-allow" => await ListAllowAsync(args, services, ct).ConfigureAwait(false),
                "findings" => await FindingsAsync(args, services, ct).ConfigureAwait(false),
                "cost" => await CostAsync(services, ct).ConfigureAwait(false),
                "help" or "--help" or "-h" => PrintHelp(),
                _ => Unknown(verb),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunOnceAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var orch = scope.ServiceProvider.GetRequiredService<ScanOrchestrator>();
        var result = await orch.RunOnceAsync(ct).ConfigureAwait(false);

        Console.WriteLine($"Scan {result.ScanId} done in {result.Duration.TotalSeconds:F1}s. " +
            $"{result.ToolResults.Count} tools, {result.Findings.Count} findings, {result.Errors.Count} errors.");
        foreach (var err in result.Errors)
        {
            Console.Error.WriteLine($"  ! {err}");
        }
        return result.Errors.Count == 0 ? 0 : 2;
    }

    private static async Task<int> AllowAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: allow <tool> <identity_key> [note]");
            return 1;
        }
        var tool = args[1];
        var key = args[2];
        var note = args.Length >= 4 ? string.Join(' ', args.Skip(3)) : null;

        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IKnownGoodRepository>();
        await repo.AddAsync(new KnownGoodEntry(tool, key, note, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        Console.WriteLine($"allowed: {tool} / {key}");
        return 0;
    }

    private static async Task<int> DenyAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: deny <tool> <identity_key>");
            return 1;
        }
        var tool = args[1];
        var key = args[2];

        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IKnownGoodRepository>();
        var removed = await repo.RemoveAsync(tool, key, ct).ConfigureAwait(false);
        Console.WriteLine(removed ? $"removed: {tool} / {key}" : $"no entry: {tool} / {key}");
        return removed ? 0 : 1;
    }

    private static async Task<int> ListAllowAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var tool = args.Length >= 2 ? args[1] : null;
        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IKnownGoodRepository>();
        var entries = await repo.ListAsync(tool, ct).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            Console.WriteLine("(no allow-list entries)");
            return 0;
        }
        foreach (var e in entries)
        {
            var noteSuffix = string.IsNullOrEmpty(e.Note) ? "" : $"  # {e.Note}";
            Console.WriteLine($"{e.AddedAt:u}  {e.ToolName,-22}  {e.IdentityKey}{noteSuffix}");
        }
        return 0;
    }

    private static async Task<int> FindingsAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var limit = 50;
        Severity? minSeverity = null;
        if (args.Length >= 2 && int.TryParse(args[1], out var parsedLimit))
        {
            limit = parsedLimit;
        }
        if (args.Length >= 3 && Enum.TryParse<Severity>(args[2], ignoreCase: true, out var parsedSev))
        {
            minSeverity = parsedSev;
        }

        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScanRepository>();
        var findings = await repo.GetFindingsAsync(limit, minSeverity, ct).ConfigureAwait(false);
        foreach (var f in findings)
        {
            Console.WriteLine(JsonSerializer.Serialize(f, JsonOptions));
        }
        return 0;
    }

    private static async Task<int> CostAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ICostLedger>();
        var budget = await ledger.GetBudgetAsync(ct).ConfigureAwait(false);
        Console.WriteLine($"Today (UTC {budget.DayUtc:yyyy-MM-dd}): ${budget.SpentUsd:F4} / ${budget.CapUsd:F2}  (remaining ${budget.RemainingUsd:F4})");
        if (budget.IsExhausted)
        {
            Console.WriteLine("Status: CAP REACHED — triage paused for the day.");
        }
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            MacMonitor — Phase-3 worker

            Usage:
              dotnet run --project src/MacMonitor.Worker                        run the long-lived worker
              dotnet run --project src/MacMonitor.Worker -- once                single scan, then exit
              dotnet run --project src/MacMonitor.Worker -- allow <tool> <id> [note]
              dotnet run --project src/MacMonitor.Worker -- deny  <tool> <id>
              dotnet run --project src/MacMonitor.Worker -- list-allow [tool]
              dotnet run --project src/MacMonitor.Worker -- findings [limit=50] [min-severity=Info]
              dotnet run --project src/MacMonitor.Worker -- cost                today's Anthropic spend vs. cap

            Tool names: list_processes, list_launch_agents, network_connections, recent_downloads
            Severities: Info, Low, Medium, High
            """);
        return 0;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"unknown command: {verb}. Try 'help'.");
        return 1;
    }
}
