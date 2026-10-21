using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Experiments.Diagnostics;

public sealed class RuntimeSampler : BackgroundService
{
    private readonly object _gate = new();
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, RawRuntimeValue> _values = new();
    private readonly HashSet<string> _observable = new();
    private readonly Queue<RuntimePoint> _history = new();
    public string Phase { get; set; } = "idle";

    public RuntimeSampler()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "System.Runtime")
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dimensions = new Dictionary<string, string>();
        foreach (var tag in tags)
            dimensions[tag.Key] = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? "";
        var key = instrument.Name + "|" + string.Join("|", dimensions.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
        lock (_gate)
        {
            if (instrument.IsObservable)
                _observable.Add(key);
            else if (instrument is Counter<double> or Counter<long> or Counter<int>)
                value += _values.GetValueOrDefault(key)?.Value ?? 0;
            _values[key] = new(instrument.Name, value, instrument.Unit ?? "", dimensions);
        }
    }

    public RuntimePoint Sample(string? phase = null)
    {
        lock (_gate)
        {
            foreach (var key in _observable)
                _values.Remove(key);
            _listener.RecordObservableInstruments();
            var sample = new RuntimePoint(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow, phase ?? Phase,
                _values.Values.ToArray());
            _history.Enqueue(sample);
            while (_history.Count > 1200)
                _history.Dequeue();
            return sample;
        }
    }

    public RuntimePoint[] Since(long ticks)
    {
        lock (_gate)
            return _history.Where(point => point.MonotonicTicks >= ticks).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            Sample();
    }

    public override void Dispose()
    {
        _listener.Dispose();
        base.Dispose();
    }
}
