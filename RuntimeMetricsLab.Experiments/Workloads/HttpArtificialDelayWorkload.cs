using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Abstractions;

namespace RuntimeMetricsLab.Experiments.Workloads;

/// <summary>Repeatedly calls a loopback endpoint that waits a configured delay before responding.</summary>
public sealed class HttpArtificialDelayWorkload(ILoopbackHttpClientProvider loopback) : IExperimentWorkload
{
    public ExperimentId Id => ExperimentId.HttpArtificialDelay;

    public Task ExecuteAsync(ExperimentRunContext context, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(context.Parameters.DurationSeconds ?? 10);
        var delayMilliseconds = Math.Max(0, context.Parameters.DelayMilliseconds ?? 200);
        var client = loopback.GetPooledClient();
        var uri = new Uri(loopback.BaseAddress, $"/workloads/http/delay?ms={delayMilliseconds}");

        return WorkloadDurationLoop.RunAsync(duration, cancellationToken, async token =>
        {
            using var response = await client.GetAsync(uri, token).ConfigureAwait(false);
            context.Metrics.HttpRequest(response.IsSuccessStatusCode ? "success" : "failure");
            context.Metrics.WorkIterations();
        });
    }
}
