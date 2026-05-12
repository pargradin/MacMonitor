using MacMonitor.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MacMonitor.Tools;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMacMonitorTools(this IServiceCollection services)
    {
        // Scan tools — orchestrator runs all of these every scan.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IScanTool, ListProcessesTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IScanTool, ListLaunchAgentsTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IScanTool, NetworkConnectionsTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IScanTool, RecentDownloadsTool>());

        // Agent tools — invoked on demand by the Phase-3 triage loop. Stubs in this round.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentTool, ProcessDetailTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentTool, ReadLaunchPlistTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentTool, VerifySignatureTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentTool, HashFileTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentTool, QuarantineEventsTool>());
        return services;
    }
}
