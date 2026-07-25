using RuntimeMetricsLab.Experiments.Diagnostics;

namespace RuntimeMetricsLab.ApiService.Endpoints;

public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/metrics/cpu/latest", (CpuMetricsMonitor monitor) => Results.Ok(monitor.GetLatest()))
            .WithName("GetLatestCpuMetrics")
            .WithTags("Metrics");

        return app;
    }
}
