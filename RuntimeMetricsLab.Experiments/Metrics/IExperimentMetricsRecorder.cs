using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Contracts.Runs;

namespace RuntimeMetricsLab.Experiments.Metrics;

/// <summary>
/// Per-run façade over <see cref="ExperimentMetrics"/> handed to workloads so they never need to
/// think about tagging. Deliberately does NOT accept or expose a run id: every instrument this
/// records against is tagged only with the experiment id (and, where relevant, an outcome/kind
/// label), both of which are fixed, low-cardinality enums.
/// </summary>
public interface IExperimentMetricsRecorder
{
    void WorkIterations(long count = 1);

    void BytesAllocated(long bytes);

    void HttpRequest(string outcome);

    void GrpcMessage(string kind);

    void CustomSample(double value);

    IReadOnlyList<RunMetricSample> Snapshot();
}

internal sealed class ExperimentMetricsRecorder(ExperimentMetrics metrics, ExperimentId experimentId) : IExperimentMetricsRecorder
{
    private readonly object _gate = new();
    private long _workIterations;
    private long _bytesAllocated;
    private long _httpSuccesses;
    private long _httpFailures;
    private long _grpcUnaryMessages;
    private long _grpcUnaryErrors;
    private long _grpcStreamMessages;
    private long _customSampleCount;
    private double _customSampleTotal;
    private double _customSampleMinimum = double.PositiveInfinity;
    private double _customSampleMaximum = double.NegativeInfinity;

    public void WorkIterations(long count = 1)
    {
        metrics.WorkIterations(experimentId, count);
        Interlocked.Add(ref _workIterations, count);
    }

    public void BytesAllocated(long bytes)
    {
        metrics.BytesAllocated(experimentId, bytes);
        Interlocked.Add(ref _bytesAllocated, bytes);
    }

    public void HttpRequest(string outcome)
    {
        metrics.HttpRequest(experimentId, outcome);
        if (string.Equals(outcome, "success", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _httpSuccesses);
        }
        else
        {
            Interlocked.Increment(ref _httpFailures);
        }
    }

    public void GrpcMessage(string kind)
    {
        metrics.GrpcMessage(experimentId, kind);
        switch (kind)
        {
            case "unary":
                Interlocked.Increment(ref _grpcUnaryMessages);
                break;
            case "unary_error":
                Interlocked.Increment(ref _grpcUnaryErrors);
                break;
            default:
                Interlocked.Increment(ref _grpcStreamMessages);
                break;
        }
    }

    public void CustomSample(double value)
    {
        metrics.CustomSample(experimentId, value);
        lock (_gate)
        {
            _customSampleCount++;
            _customSampleTotal += value;
            _customSampleMinimum = Math.Min(_customSampleMinimum, value);
            _customSampleMaximum = Math.Max(_customSampleMaximum, value);
        }
    }

    public IReadOnlyList<RunMetricSample> Snapshot()
    {
        var samples = new List<RunMetricSample>();
        AddIfNonZero(samples, "work.iterations", Interlocked.Read(ref _workIterations), "{iteration}");
        AddIfNonZero(samples, "work.bytes_allocated", Interlocked.Read(ref _bytesAllocated), "By");
        AddIfNonZero(samples, "http.requests.success", Interlocked.Read(ref _httpSuccesses), "{request}");
        AddIfNonZero(samples, "http.requests.failure", Interlocked.Read(ref _httpFailures), "{request}");
        AddIfNonZero(samples, "grpc.messages.unary", Interlocked.Read(ref _grpcUnaryMessages), "{message}");
        AddIfNonZero(samples, "grpc.messages.unary_error", Interlocked.Read(ref _grpcUnaryErrors), "{message}");
        AddIfNonZero(samples, "grpc.messages.stream", Interlocked.Read(ref _grpcStreamMessages), "{message}");

        lock (_gate)
        {
            if (_customSampleCount > 0)
            {
                samples.Add(new("custom.samples.count", _customSampleCount, "{sample}"));
                samples.Add(new("custom.samples.average", _customSampleTotal / _customSampleCount, "1"));
                samples.Add(new("custom.samples.minimum", _customSampleMinimum, "1"));
                samples.Add(new("custom.samples.maximum", _customSampleMaximum, "1"));
            }
        }

        return samples;
    }

    private static void AddIfNonZero(List<RunMetricSample> samples, string name, long value, string unit)
    {
        if (value != 0)
        {
            samples.Add(new(name, value, unit));
        }
    }
}
