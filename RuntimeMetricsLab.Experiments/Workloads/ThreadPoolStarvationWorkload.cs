using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Abstractions;

namespace RuntimeMetricsLab.Experiments.Workloads;

/// <summary>
/// Occupies a bounded number of thread pool worker threads with blocking sleeps for the run
/// duration, simulating thread pool starvation without touching global min/max thread pool
/// configuration.
/// </summary>
public sealed class ThreadPoolStarvationWorkload : IExperimentWorkload
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public ExperimentId Id => ExperimentId.ThreadPoolStarvation;

    public Task ExecuteAsync(ExperimentRunContext context, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(context.Parameters.DurationSeconds ?? 10);
        var blockedThreadCount = Math.Max(1, context.Parameters.BlockedThreadCount ?? 8);

        var blockers = Enumerable.Range(0, blockedThreadCount)
            .Select(_ => Task.Run(() => BlockThread(context, duration, cancellationToken), cancellationToken));

        return Task.WhenAll(blockers);
    }

    private static void BlockThread(ExperimentRunContext context, TimeSpan duration, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(PollInterval);
            context.Metrics.WorkIterations();
        }
    }
}
