using RuntimeMetricsLab.Experiments.Diagnostics;

namespace RuntimeMetricsLab.ApiService.Endpoints;

public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/metrics/cpu/latest", (CpuMetricsMonitor monitor) => Results.Ok(monitor.GetLatest()))
            .WithName("GetLatestCpuMetrics")
            .WithTags("Metrics");
        app.MapGet("/api/metrics/runtime/latest", (RuntimeSampler sampler) => Results.Ok(sampler.Sample()));
        app.MapGet("/api/metrics/runtime/history", (RuntimeSampler sampler) =>
            Results.Ok(sampler.Since(System.Diagnostics.Stopwatch.GetTimestamp() - 60 * System.Diagnostics.Stopwatch.Frequency)));

        return app;
    }
}
