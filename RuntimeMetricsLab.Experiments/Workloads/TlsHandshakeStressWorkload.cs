using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Abstractions;

namespace RuntimeMetricsLab.Experiments.Workloads;

/// <summary>
/// Forces a brand-new TCP connection and TLS handshake on every iteration by requesting a fresh
/// client per call, against this service's own HTTPS loopback endpoint. Runs <c>WorkerCount</c>
/// concurrent loops to also exercise concurrent handshake load.
/// </summary>
public sealed class TlsHandshakeStressWorkload(ILoopbackHttpClientProvider loopback) : IExperimentWorkload
{
    public ExperimentId Id => ExperimentId.TlsHandshakeStress;

    public Task ExecuteAsync(ExperimentRunContext context, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(context.Parameters.DurationSeconds ?? 10);
        var workerCount = Math.Max(1, context.Parameters.WorkerCount ?? 1);
        var uri = new Uri(loopback.BaseAddress, "/workloads/tls/probe");

        var workers = Enumerable.Range(0, workerCount).Select(_ => WorkloadDurationLoop.RunAsync(duration, cancellationToken, async token =>
        {
            using var client = loopback.CreateFreshConnectionClient();
            using var response = await client.GetAsync(uri, token).ConfigureAwait(false);
            context.Metrics.HttpRequest(response.IsSuccessStatusCode ? "success" : "failure");
            context.Metrics.WorkIterations();
        }));

        return Task.WhenAll(workers);
    }
}
