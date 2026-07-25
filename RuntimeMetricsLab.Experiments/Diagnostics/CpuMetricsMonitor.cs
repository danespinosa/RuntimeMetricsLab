using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using RuntimeMetricsLab.Contracts.Diagnostics;

namespace RuntimeMetricsLab.Experiments.Diagnostics;

/// <summary>Listens to the built-in System.Runtime process CPU and working-set instruments.</summary>
public sealed class CpuMetricsMonitor : BackgroundService
{
    private const string RuntimeMeterName = "System.Runtime";
    private const string CpuTimeInstrumentName = "dotnet.process.cpu.time";
    private const string WorkingSetInstrumentName = "dotnet.process.memory.working_set";

    private readonly object _gate = new();
    private readonly MeterListener _listener;
    private double _userCpuSeconds;
    private double _systemCpuSeconds;
    private long _workingSetBytes;
    private double _previousCpuSeconds;
    private DateTimeOffset? _previousTimestamp;
    private RuntimeMetricSample _latest = new(DateTimeOffset.UtcNow, 0, 0, Environment.WorkingSet, Environment.ProcessorCount);

    public CpuMetricsMonitor()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == RuntimeMeterName
                    && instrument.Name is CpuTimeInstrumentName or WorkingSetInstrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<double>(RecordDouble);
        _listener.SetMeasurementEventCallback<long>(RecordLong);
        _listener.SetMeasurementEventCallback<int>(RecordInt);
        _listener.Start();
    }

    public RuntimeMetricSample GetLatest()
    {
        lock (_gate)
        {
            return _latest;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            _listener.RecordObservableInstruments();
            CaptureSample();
        }
    }

    private void RecordDouble(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (instrument.Name != CpuTimeInstrumentName)
        {
            return;
        }

        var mode = FindTag(tags, "cpu.mode");
        lock (_gate)
        {
            if (mode == "system")
            {
                _systemCpuSeconds = measurement;
            }
            else if (mode == "user")
            {
                _userCpuSeconds = measurement;
            }
        }
    }

    private void RecordLong(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (instrument.Name == WorkingSetInstrumentName)
        {
            lock (_gate)
            {
                _workingSetBytes = measurement;
            }
        }
    }

    private void RecordInt(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        RecordLong(instrument, measurement, tags, state);

    private void CaptureSample()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var cpuSeconds = _userCpuSeconds + _systemCpuSeconds;
            var utilization = 0d;
            if (_previousTimestamp is { } previousTimestamp)
            {
                var elapsedSeconds = (now - previousTimestamp).TotalSeconds;
                if (elapsedSeconds > 0)
                {
                    var cpuDelta = Math.Max(0, cpuSeconds - _previousCpuSeconds);
                    utilization = Math.Clamp(
                        cpuDelta / elapsedSeconds / Math.Max(1, Environment.ProcessorCount) * 100,
                        0,
                        100);
                }
            }

            _latest = new RuntimeMetricSample(
                now,
                utilization,
                cpuSeconds,
                _workingSetBytes > 0 ? _workingSetBytes : Environment.WorkingSet,
                Environment.ProcessorCount);
            _previousCpuSeconds = cpuSeconds;
            _previousTimestamp = now;
        }
    }

    private static string? FindTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == name)
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    public override void Dispose()
    {
        _listener.Dispose();
        base.Dispose();
    }
}
