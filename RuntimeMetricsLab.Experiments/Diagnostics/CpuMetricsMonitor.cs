using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using RuntimeMetricsLab.Contracts.Attribution;
using RuntimeMetricsLab.Contracts.Diagnostics;

namespace RuntimeMetricsLab.Experiments.Diagnostics;

/// <summary>Compatibility view over the shared monotonic runtime sampler.</summary>
public sealed class CpuMetricsMonitor(RuntimeSampler sampler) : BackgroundService
{
    private readonly object _gate = new();
    private RuntimePoint? _previous;
    private long _lastTicks;
    private RuntimeMetricSample _latest = new(DateTimeOffset.UtcNow, 0, 0, 0, Environment.ProcessorCount);

    public RuntimeMetricSample GetLatest()
    {
        lock (_gate)
            return _latest with { IsStale = _lastTicks == 0 || Stopwatch.GetElapsedTime(_lastTicks) > TimeSpan.FromSeconds(3) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var current = sampler.Sample();
            var cpu = AttributionMetrics.Value(current, "dotnet.process.cpu.time", "cpu.mode", "user") +
                AttributionMetrics.Value(current, "dotnet.process.cpu.time", "cpu.mode", "system");
            var workingSet = AttributionMetrics.Value(current, "dotnet.process.memory.working_set");
            double? cores = null;
            if (_previous is { } previous)
            {
                var window = new MeasurementWindow(previous, current, Stopwatch.Frequency, true);
                var delta = AttributionMetrics.Delta(window, "dotnet.process.cpu.time", "cpu.mode", "user") +
                    AttributionMetrics.Delta(window, "dotnet.process.cpu.time", "cpu.mode", "system");
                var seconds = Stopwatch.GetElapsedTime(previous.MonotonicTicks, current.MonotonicTicks).TotalSeconds;
                if (seconds > 0) cores = delta / seconds;
            }
            lock (_gate)
            {
                // Legacy numeric fields remain compatible; availability flags distinguish absent observations.
                _latest = new(current.Utc, cores * 100 / Environment.ProcessorCount ?? 0,
                    cpu ?? 0, (long)(workingSet ?? 0), Environment.ProcessorCount,
                    cpu.HasValue, workingSet.HasValue, cores.HasValue, false, cores);
                _lastTicks = current.MonotonicTicks;
                _previous = current;
            }
        }
    }
}
