using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Abstractions;

namespace RuntimeMetricsLab.Experiments.Workloads;

/// <summary>
/// Saturates <c>WorkerCount</c> tasks with busy-loop computation at roughly <c>IntensityPercent</c>
/// duty cycle for the configured duration, using short sleeps to yield CPU back between bursts.
/// </summary>
public sealed class CpuBurnWorkload : IExperimentWorkload
{
    public ExperimentId Id => ExperimentId.CpuBurn;

    public async Task ExecuteAsync(ExperimentRunContext context, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(context.Parameters.DurationSeconds ?? 10);
        var intensity = Math.Clamp(context.Parameters.IntensityPercent ?? 50, 1, 100);
        var workerCount = Math.Max(1, context.Parameters.WorkerCount ?? 1);

        var burst = TimeSpan.FromMilliseconds(20 * intensity / 100.0);
        var idle = TimeSpan.FromMilliseconds(20) - burst;

        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => Burn(context, duration, burst, idle, cancellationToken), cancellationToken));

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private static void Burn(ExperimentRunContext context, TimeSpan duration, TimeSpan burst, TimeSpan idle, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + duration;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            sw.Restart();
            long spins = 0;
            while (sw.Elapsed < burst)
            {
                spins++;
            }

            context.Metrics.WorkIterations(spins);

            if (idle > TimeSpan.Zero)
            {
                Thread.Sleep(idle);
            }
        }
    }
}
