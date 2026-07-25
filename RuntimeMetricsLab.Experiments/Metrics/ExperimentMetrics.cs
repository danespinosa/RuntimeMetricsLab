using System.Diagnostics;
using System.Diagnostics.Metrics;
using RuntimeMetricsLab.Contracts.Catalog;

namespace RuntimeMetricsLab.Experiments.Metrics;

/// <summary>
/// Owns the lab's single custom <see cref="Meter"/> (name: <see cref="MeterName"/>) and every
/// instrument published on it. Registered as a singleton so the meter and its instruments live
/// for the lifetime of the process.
///
/// Tagging rule: every instrument here is tagged with at most <c>experiment.id</c> (11 possible
/// values), <c>experiment.category</c> (7 possible values), <c>result</c>/<c>outcome</c>/<c>kind</c>
/// (a handful of fixed strings). Run ids (GUIDs) are intentionally never used as tags because they
/// are unbounded cardinality and would blow up time-series storage in any metrics backend.
/// </summary>
public sealed class ExperimentMetrics : IDisposable
{
    public const string MeterName = "RuntimeMetricsLab.Experiments";

    private readonly Meter _meter;
    private readonly Counter<long> _runsStarted;
    private readonly Counter<long> _runsCompleted;
    private readonly UpDownCounter<long> _activeRuns;
    private readonly Histogram<double> _runDurationSeconds;
    private readonly Counter<long> _workIterations;
    private readonly Counter<long> _bytesAllocated;
    private readonly Counter<long> _httpRequests;
    private readonly Counter<long> _grpcMessages;
    private readonly Histogram<double> _customSamples;

    public ExperimentMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        _runsStarted = _meter.CreateCounter<long>(
            "experiment.runs.started", unit: "{run}", description: "Number of experiment runs started.");
        _runsCompleted = _meter.CreateCounter<long>(
            "experiment.runs.completed", unit: "{run}", description: "Number of experiment runs that reached a terminal state.");
        _activeRuns = _meter.CreateUpDownCounter<long>(
            "experiment.runs.active", unit: "{run}", description: "Number of experiment runs currently executing.");
        _runDurationSeconds = _meter.CreateHistogram<double>(
            "experiment.run.duration", unit: "s", description: "Wall-clock duration of a completed experiment run.");
        _workIterations = _meter.CreateCounter<long>(
            "experiment.work.iterations", unit: "{iteration}", description: "Units of work performed by a running experiment (loop iterations, requests, etc.).");
        _bytesAllocated = _meter.CreateCounter<long>(
            "experiment.work.bytes_allocated", unit: "By", description: "Bytes deliberately allocated by memory/GC experiments.");
        _httpRequests = _meter.CreateCounter<long>(
            "experiment.work.http_requests", unit: "{request}", description: "Loopback HTTP requests issued by HTTP/TLS experiments.");
        _grpcMessages = _meter.CreateCounter<long>(
            "experiment.work.grpc_messages", unit: "{message}", description: "gRPC messages exchanged by gRPC experiments.");
        _customSamples = _meter.CreateHistogram<double>(
            "experiment.work.custom_sample", unit: "1", description: "Sample values emitted by the custom OTel metrics demo experiment.");
    }

    public void RunStarted(ExperimentId id)
    {
        _runsStarted.Add(1, new KeyValuePair<string, object?>("experiment.id", id.ToString()));
        _activeRuns.Add(1, new KeyValuePair<string, object?>("experiment.id", id.ToString()));
    }

    public void RunCompleted(ExperimentId id, string result, TimeSpan duration)
    {
        TagList tags =
        [
            new("experiment.id", id.ToString()),
            new("result", result),
        ];
        _runsCompleted.Add(1, tags);
        _runDurationSeconds.Record(duration.TotalSeconds, tags);
        _activeRuns.Add(-1, new KeyValuePair<string, object?>("experiment.id", id.ToString()));
    }

    public void WorkIterations(ExperimentId id, long count) =>
        _workIterations.Add(count, new KeyValuePair<string, object?>("experiment.id", id.ToString()));

    public void BytesAllocated(ExperimentId id, long bytes) =>
        _bytesAllocated.Add(bytes, new KeyValuePair<string, object?>("experiment.id", id.ToString()));

    public void HttpRequest(ExperimentId id, string outcome)
    {
        TagList tags =
        [
            new("experiment.id", id.ToString()),
            new("outcome", outcome),
        ];
        _httpRequests.Add(1, tags);
    }

    public void GrpcMessage(ExperimentId id, string kind)
    {
        TagList tags =
        [
            new("experiment.id", id.ToString()),
            new("kind", kind),
        ];
        _grpcMessages.Add(1, tags);
    }

    public void CustomSample(ExperimentId id, double value) =>
        _customSamples.Record(value, new KeyValuePair<string, object?>("experiment.id", id.ToString()));

    public IExperimentMetricsRecorder CreateRecorder(ExperimentId id) => new ExperimentMetricsRecorder(this, id);

    public void Dispose() => _meter.Dispose();
}
