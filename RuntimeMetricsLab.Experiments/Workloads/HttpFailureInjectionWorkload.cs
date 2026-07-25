using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Abstractions;

namespace RuntimeMetricsLab.Experiments.Workloads;

/// <summary>Repeatedly calls a loopback endpoint that fails with a configured probability, recording outcomes as metrics.</summary>
public sealed class HttpFailureInjectionWorkload(ILoopbackHttpClientProvider loopback) : IExperimentWorkload
{
    public ExperimentId Id => ExperimentId.HttpFailureInjection;

    public Task ExecuteAsync(ExperimentRunContext context, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(context.Parameters.DurationSeconds ?? 10);
        var failureRatePercent = Math.Clamp(context.Parameters.FailureRatePercent ?? 20, 0, 100);
        var client = loopback.GetPooledClient();
        var uri = new Uri(loopback.BaseAddress, $"/workloads/http/failure?ratePercent={failureRatePercent}");

        return WorkloadDurationLoop.RunAsync(duration, cancellationToken, async token =>
        {
            using var response = await client.GetAsync(uri, token).ConfigureAwait(false);
            context.Metrics.HttpRequest(response.IsSuccessStatusCode ? "success" : "failure");
            context.Metrics.WorkIterations();
        });
    }
}
