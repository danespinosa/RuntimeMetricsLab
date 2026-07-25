using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Abstractions;

namespace RuntimeMetricsLab.Experiments.Workloads;

/// <summary>
/// Allocates and immediately discards byte buffers of a fixed size at a target rate, generating
/// Gen0/Gen1 GC pressure without retaining references (so peak managed memory stays bounded to
/// roughly one buffer at a time plus GC overhead).
/// </summary>
public sealed class MemoryAllocationChurnWorkload : IExperimentWorkload
{
    public ExperimentId Id => ExperimentId.MemoryAllocationChurn;

    public async Task ExecuteAsync(ExperimentRunContext context, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(context.Parameters.DurationSeconds ?? 10);
        var allocationSize = (int)Math.Clamp(context.Parameters.AllocationSizeBytes ?? 64 * 1024, 1, int.MaxValue);
        var allocationsPerSecond = Math.Max(1, context.Parameters.AllocationsPerSecond ?? 200);
        var interval = TimeSpan.FromSeconds(1.0 / allocationsPerSecond);

        await WorkloadDurationLoop.RunAsync(duration, cancellationToken, async token =>
        {
            var buffer = new byte[allocationSize];
            // Touch the buffer so the allocation cannot be optimized away and pages are actually committed.
            buffer[0] = 1;
            buffer[^1] = 1;
            context.Metrics.BytesAllocated(allocationSize);
            context.Metrics.WorkIterations();

            await Task.Delay(interval, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
