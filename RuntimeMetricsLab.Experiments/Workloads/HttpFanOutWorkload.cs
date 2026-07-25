using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Abstractions;

namespace RuntimeMetricsLab.Experiments.Workloads;

/// <summary>Issues a bounded number of concurrent loopback requests per iteration to exercise fan-out latency and connection pooling.</summary>
public sealed class HttpFanOutWorkload(ILoopbackHttpClientProvider loopback) : IExperimentWorkload
{
    public ExperimentId Id => ExperimentId.HttpFanOut;

    public Task ExecuteAsync(ExperimentRunContext context, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(context.Parameters.DurationSeconds ?? 10);
        var fanOutCount = Math.Max(1, context.Parameters.FanOutCount ?? 10);
        var payloadSizeBytes = Math.Max(1, context.Parameters.PayloadSizeBytes ?? 8 * 1024);
        var client = loopback.GetPooledClient();
        var uri = new Uri(loopback.BaseAddress, $"/workloads/http/payload?sizeBytes={payloadSizeBytes}");

        return WorkloadDurationLoop.RunAsync(duration, cancellationToken, async token =>
        {
            var branches = Enumerable.Range(0, fanOutCount).Select(async branchIndex =>
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                context.Metrics.HttpRequest(response.IsSuccessStatusCode ? "success" : "failure");
                context.Metrics.WorkIterations();
            });

            await Task.WhenAll(branches).ConfigureAwait(false);
        });
    }
}
