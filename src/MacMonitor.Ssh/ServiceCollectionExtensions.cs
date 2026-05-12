using MacMonitor.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MacMonitor.Ssh;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMacMonitorSsh(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<SshOptions>()
            .Bind(config.GetSection(SshOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IPrivateKeyProvider, KeychainPrivateKeyProvider>();
        services.AddSingleton<SshExecutor>();
        services.AddSingleton<ISshExecutor>(sp => sp.GetRequiredService<SshExecutor>());
        return services;
    }
}
