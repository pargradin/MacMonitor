using MacMonitor.Core.Abstractions;
using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MacMonitor.Alerts;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMacMonitorAlerts(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AlertOptions>()
            .Bind(config.GetSection(AlertOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<NotificationOptions>()
            .Bind(config.GetSection(NotificationOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAlertSink, JsonlFileSink>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAlertSink, MacOsNotificationSink>());
        return services;
    }
}
