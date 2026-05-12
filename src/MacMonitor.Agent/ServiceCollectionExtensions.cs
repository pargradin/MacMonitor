using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MacMonitor.Core.Abstractions;

namespace MacMonitor.Agent;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMacMonitorAgent(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AgentOptions>()
            .Bind(config.GetSection(AgentOptions.SectionName))
            .ValidateOnStart();

        services.TryAddSingleton<IKeychainSecretProvider, KeychainSecretProvider>();

        // Typed HttpClient for Anthropic. Configure base URL + default headers from options.
        services.AddHttpClient<AnthropicClient>((sp, http) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
            http.BaseAddress = new Uri(opts.ApiBaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(Math.Max(30, opts.WallClockBudgetSeconds));
            http.DefaultRequestHeaders.Add("anthropic-version", opts.ApiVersion);
        });

        services.TryAddSingleton<AgentLoop>();
        services.TryAddSingleton<ITriageService, TriageService>();

        return services;
    }
}
