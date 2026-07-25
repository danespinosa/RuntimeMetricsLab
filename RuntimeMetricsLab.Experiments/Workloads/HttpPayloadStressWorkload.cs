using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Abstractions;

namespace RuntimeMetricsLab.Experiments.Workloads;

/// <summary>Repeatedly requests a generated payload of a configured size from the loopback workload endpoint.</summary>
public sealed class HttpPayloadStressWorkload(ILoopbackHttpClientProvider loopback) : IExperimentWorkload
{
    public ExperimentId Id => ExperimentId.HttpPayloadStress;

    public Task ExecuteAsync(ExperimentRunContext context, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(context.Parameters.DurationSeconds ?? 10);
        var payloadSizeBytes = Math.Max(1, context.Parameters.PayloadSizeBytes ?? 64 * 1024);
        var client = loopback.GetPooledClient();
        var uri = new Uri(loopback.BaseAddress, $"/workloads/http/payload?sizeBytes={payloadSizeBytes}");

        return WorkloadDurationLoop.RunAsync(duration, cancellationToken, async token =>
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            context.Metrics.HttpRequest(response.IsSuccessStatusCode ? "success" : "failure");
            context.Metrics.WorkIterations();
            _ = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        });
    }
}
