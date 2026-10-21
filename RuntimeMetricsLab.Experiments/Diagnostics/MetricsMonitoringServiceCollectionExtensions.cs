using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RuntimeMetricsLab.Experiments.Diagnostics;

public static class MetricsMonitoringServiceCollectionExtensions
{
    public static IServiceCollection AddRuntimeMetricsMonitoring(this IServiceCollection services)
    {
        services.TryAddSingleton<RuntimeSampler>();
        services.AddHostedService(static provider => provider.GetRequiredService<RuntimeSampler>());
        services.TryAddSingleton<CpuMetricsMonitor>();
        services.AddHostedService(static provider => provider.GetRequiredService<CpuMetricsMonitor>());
        return services;
    }
}
