using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RuntimeMetricsLab.Experiments.Abstractions;
using RuntimeMetricsLab.Experiments.Catalog;
using RuntimeMetricsLab.Experiments.Coordination;
using RuntimeMetricsLab.Experiments.Metrics;
using RuntimeMetricsLab.Experiments.Workloads;

namespace RuntimeMetricsLab.Experiments;

/// <summary>
/// Registers the catalog, metrics, coordinator, and every in-process workload. Host projects
/// (RuntimeMetricsLab.ApiService) must additionally register an <see cref="ILoopbackHttpClientProvider"/>
/// and an <see cref="IGrpcWorkloadInvoker"/> implementation, since those require ASP.NET Core
/// hosting / gRPC packages this project intentionally does not depend on.
/// </summary>
public static class ExperimentsServiceCollectionExtensions
{
    public static IServiceCollection AddRuntimeMetricsLabExperiments(this IServiceCollection services)
    {
        services.TryAddSingleton<IExperimentCatalog, ExperimentCatalog>();
        services.TryAddSingleton<ExperimentMetrics>();
        services.TryAddSingleton<ExperimentCoordinator>();

        services.AddSingleton<IExperimentWorkload, CpuBurnWorkload>();
        services.AddSingleton<IExperimentWorkload, MemoryAllocationChurnWorkload>();
        services.AddSingleton<IExperimentWorkload, ThreadPoolStarvationWorkload>();
        services.AddSingleton<IExperimentWorkload, HttpPayloadStressWorkload>();
        services.AddSingleton<IExperimentWorkload, HttpArtificialDelayWorkload>();
        services.AddSingleton<IExperimentWorkload, HttpFailureInjectionWorkload>();
        services.AddSingleton<IExperimentWorkload, HttpFanOutWorkload>();
        services.AddSingleton<IExperimentWorkload, TlsHandshakeStressWorkload>();
        services.AddSingleton<IExperimentWorkload, GrpcUnaryEchoWorkload>();
        services.AddSingleton<IExperimentWorkload, GrpcServerStreamingWorkload>();
        services.AddSingleton<IExperimentWorkload, CustomOtelMetricsDemoWorkload>();

        return services;
    }
}
