using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Abstractions;

namespace RuntimeMetricsLab.Experiments.Workloads;

/// <summary>
/// Emits a stream of synthetic sample values onto the lab's custom OTel Meter at a rate driven by
/// <c>IntensityPercent</c>, purely for exercising dashboards/alerts end to end.
/// </summary>
public sealed class CustomOtelMetricsDemoWorkload : IExperimentWorkload
{
    public ExperimentId Id => ExperimentId.CustomOtelMetricsDemo;

    public async Task ExecuteAsync(ExperimentRunContext context, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(context.Parameters.DurationSeconds ?? 10);
        var intensity = Math.Clamp(context.Parameters.IntensityPercent ?? 10, 1, 100);

        // 1% intensity -> ~1 sample/sec, 100% intensity -> ~50 samples/sec.
        var samplesPerSecond = Math.Max(1, intensity / 2);
        var interval = TimeSpan.FromSeconds(1.0 / samplesPerSecond);
        var random = Random.Shared;
        var iteration = 0;

        await WorkloadDurationLoop.RunAsync(duration, cancellationToken, async token =>
        {
            iteration++;
            // A gentle sine wave plus noise so consumers see a recognizable, non-trivial signal.
            var value = 50 + 25 * Math.Sin(iteration * 0.1) + random.NextDouble() * 5;
            context.Metrics.CustomSample(value);
            context.Metrics.WorkIterations();

            await Task.Delay(interval, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
