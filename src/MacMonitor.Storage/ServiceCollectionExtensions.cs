using MacMonitor.Core.Abstractions;
using MacMonitor.Storage.Differs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MacMonitor.Storage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMacMonitorStorage(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<StorageOptions>()
            .Bind(config.GetSection(StorageOptions.SectionName))
            .ValidateOnStart();

        services.TryAddSingleton<SqliteConnectionFactory>();
        services.TryAddSingleton<IBaselineStore, SqliteBaselineStore>();
        services.TryAddSingleton<IScanRepository, SqliteScanRepository>();
        services.TryAddSingleton<IKnownGoodRepository, SqliteKnownGoodRepository>();
        services.TryAddSingleton<ICostLedger, SqliteCostLedger>();

        // Register each differ as the non-generic IDiffer so the orchestrator can resolve
        // them by tool name without needing T at compile time.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiffer, ProcessInfoDiffer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiffer, LaunchItemDiffer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiffer, NetworkConnectionDiffer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiffer, DownloadedFileDiffer>());

        services.TryAddSingleton<DifferRegistry>();

        return services;
    }
}
