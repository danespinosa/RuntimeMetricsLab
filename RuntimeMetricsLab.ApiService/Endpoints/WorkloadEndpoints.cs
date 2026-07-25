using Microsoft.AspNetCore.Mvc;
using RuntimeMetricsLab.Experiments.Catalog;

namespace RuntimeMetricsLab.ApiService.Endpoints;

/// <summary>
/// The actual HTTP/TLS "targets" the HTTP and TLS lab experiments call over loopback: a
/// configurable-size payload, a configurable delay, a configurable failure rate, and a cheap probe
/// endpoint used purely to force new TLS handshakes. These are plain, stateless endpoints — all
/// bounding/validation logic (caps) lives here, independent of the experiment coordinator.
/// </summary>
public static class WorkloadEndpoints
{
    public static IEndpointRouteBuilder MapWorkloadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/workloads").WithTags("Workloads");

        group.MapGet("/http/payload", (int sizeBytes) =>
        {
            var size = Math.Clamp(sizeBytes, ExperimentLimits.MinPayloadSizeBytes, ExperimentLimits.MaxPayloadSizeBytes);
            return Results.Bytes(new byte[size], "application/octet-stream");
        })
        .WithName("GetWorkloadPayload");

        group.MapGet("/http/delay", async (int ms, CancellationToken cancellationToken) =>
        {
            var delay = Math.Clamp(ms, ExperimentLimits.MinDelayMilliseconds, ExperimentLimits.MaxDelayMilliseconds);
            await Task.Delay(delay, cancellationToken);
            return Results.Ok(new { delayedMilliseconds = delay });
        })
        .WithName("GetWorkloadDelay");

        group.MapGet("/http/failure", (int ratePercent) =>
        {
            var rate = Math.Clamp(ratePercent, ExperimentLimits.MinFailureRatePercent, ExperimentLimits.MaxFailureRatePercent);
            if (Random.Shared.Next(100) < rate)
            {
                return Results.Problem(
                    title: "Injected failure",
                    detail: $"This response was deliberately failed to simulate a {rate}% error rate.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new { failureRatePercent = rate });
        })
        .WithName("GetWorkloadFailure");

        group.MapGet("/tls/probe", () => Results.Ok())
            .WithName("GetTlsProbe");

        return app;
    }
}
