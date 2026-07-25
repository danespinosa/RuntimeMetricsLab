using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Contracts.Runs;

namespace RuntimeMetricsLab.Experiments.Catalog;

/// <summary>
/// Hard-coded metadata for all eleven lab experiments. This is the single source of truth for
/// catalog entries, parameter bounds, and presets; <c>ExperimentCatalogTests</c> asserts its
/// internal consistency (unique ids, bounds within <see cref="ExperimentLimits"/>, etc.).
/// </summary>
public sealed class ExperimentCatalog : IExperimentCatalog
{
    private readonly Dictionary<ExperimentId, ExperimentCatalogEntry> _entries;

    public ExperimentCatalog()
    {
        var duration = new ParameterBound(ExperimentLimits.MinDurationSeconds, 30, 10);

        _entries = new List<ExperimentCatalogEntry>
        {
            new(
                ExperimentId.CpuBurn,
                "CPU Burn",
                ExperimentCategory.Cpu,
                "Saturates a bounded number of worker tasks with pure computation to raise CPU utilization on demand.",
                "Consumes real CPU on the host. Bounded by worker count and duration; safe for shared dev/test hosts at default settings.",
                duration,
                new Dictionary<string, ParameterBound>
                {
                    ["intensityPercent"] = new(ExperimentLimits.MinIntensityPercent, ExperimentLimits.MaxIntensityPercent, 50),
                    ["workerCount"] = new(ExperimentLimits.MinWorkerCount, ExperimentLimits.MaxWorkerCount, Math.Min(2, ExperimentLimits.MaxWorkerCount)),
                },
                new List<ExperimentPreset>
                {
                    new("light", "Light", "Low intensity, single worker.", new ExperimentParameters { DurationSeconds = 5, IntensityPercent = 25, WorkerCount = 1 }),
                    new("moderate", "Moderate", "Half intensity across two workers.", new ExperimentParameters { DurationSeconds = 10, IntensityPercent = 50, WorkerCount = 2 }),
                }),

            new(
                ExperimentId.MemoryAllocationChurn,
                "Memory Allocation Churn",
                ExperimentCategory.Memory,
                "Repeatedly allocates and discards byte buffers to generate Gen0/Gen1 GC pressure.",
                "Bounded per-allocation size and rate keep peak managed heap growth small; triggers frequent (but short) GCs.",
                duration,
                new Dictionary<string, ParameterBound>
                {
                    ["allocationSizeBytes"] = new((int)ExperimentLimits.MinAllocationSizeBytes, (int)ExperimentLimits.MaxAllocationSizeBytes, 64 * 1024),
                    ["allocationsPerSecond"] = new(ExperimentLimits.MinAllocationsPerSecond, ExperimentLimits.MaxAllocationsPerSecond, 200),
                },
                new List<ExperimentPreset>
                {
                    new("light", "Light", "Small, infrequent allocations.", new ExperimentParameters { DurationSeconds = 5, AllocationSizeBytes = 16 * 1024, AllocationsPerSecond = 50 }),
                    new("churn", "Heavy Churn", "Larger, frequent allocations to force more GCs.", new ExperimentParameters { DurationSeconds = 10, AllocationSizeBytes = 256 * 1024, AllocationsPerSecond = 500 }),
                }),

            new(
                ExperimentId.ThreadPoolStarvation,
                "Thread Pool Starvation",
                ExperimentCategory.ThreadPool,
                "Blocks a bounded number of thread pool worker threads synchronously to simulate starvation and queue backlog.",
                "Blocked thread count and duration are capped; does not modify global min/max thread pool settings.",
                duration,
                new Dictionary<string, ParameterBound>
                {
                    ["blockedThreadCount"] = new(ExperimentLimits.MinBlockedThreadCount, ExperimentLimits.MaxBlockedThreadCount, 8),
                },
                new List<ExperimentPreset>
                {
                    new("light", "Light", "A handful of blocked threads.", new ExperimentParameters { DurationSeconds = 5, BlockedThreadCount = 4 }),
                    new("congested", "Congested", "Many blocked threads to visibly delay queued work.", new ExperimentParameters { DurationSeconds = 10, BlockedThreadCount = 24 }),
                }),

            new(
                ExperimentId.HttpPayloadStress,
                "HTTP Payload Stress",
                ExperimentCategory.Http,
                "Repeatedly requests a generated payload of a given size from the loopback workload endpoint.",
                "Payload size and request rate (via duration) are capped; traffic stays on loopback.",
                duration,
                new Dictionary<string, ParameterBound>
                {
                    ["payloadSizeBytes"] = new(ExperimentLimits.MinPayloadSizeBytes, ExperimentLimits.MaxPayloadSizeBytes, 64 * 1024),
                },
                new List<ExperimentPreset>
                {
                    new("small", "Small Payloads", "Frequent small responses.", new ExperimentParameters { DurationSeconds = 5, PayloadSizeBytes = 4 * 1024 }),
                    new("large", "Large Payloads", "Fewer, larger responses.", new ExperimentParameters { DurationSeconds = 10, PayloadSizeBytes = 1024 * 1024 }),
                }),

            new(
                ExperimentId.HttpArtificialDelay,
                "HTTP Artificial Delay",
                ExperimentCategory.Http,
                "Repeatedly calls a loopback endpoint that waits a configured delay before responding.",
                "Delay is capped so a single request can never hang indefinitely.",
                duration,
                new Dictionary<string, ParameterBound>
                {
                    ["delayMilliseconds"] = new(ExperimentLimits.MinDelayMilliseconds, ExperimentLimits.MaxDelayMilliseconds, 200),
                },
                new List<ExperimentPreset>
                {
                    new("snappy", "Snappy", "Barely noticeable delay.", new ExperimentParameters { DurationSeconds = 5, DelayMilliseconds = 50 }),
                    new("sluggish", "Sluggish", "Noticeable latency on every call.", new ExperimentParameters { DurationSeconds = 10, DelayMilliseconds = 1_500 }),
                }),

            new(
                ExperimentId.HttpFailureInjection,
                "HTTP Failure Injection",
                ExperimentCategory.Http,
                "Repeatedly calls a loopback endpoint that fails with a configured probability, to observe error-rate metrics.",
                "Failure responses are synthetic 5xx status codes from a local endpoint; no external systems are affected.",
                duration,
                new Dictionary<string, ParameterBound>
                {
                    ["failureRatePercent"] = new(ExperimentLimits.MinFailureRatePercent, ExperimentLimits.MaxFailureRatePercent, 20),
                },
                new List<ExperimentPreset>
                {
                    new("occasional", "Occasional Failures", "Low failure rate.", new ExperimentParameters { DurationSeconds = 5, FailureRatePercent = 10 }),
                    new("unstable", "Unstable", "High failure rate.", new ExperimentParameters { DurationSeconds = 10, FailureRatePercent = 60 }),
                }),

            new(
                ExperimentId.HttpFanOut,
                "HTTP Fan-Out",
                ExperimentCategory.Http,
                "Issues a bounded number of concurrent loopback requests per iteration to exercise connection pooling and fan-out latency.",
                "Concurrent request count per iteration is capped; all requests target this same process over loopback.",
                duration,
                new Dictionary<string, ParameterBound>
                {
                    ["fanOutCount"] = new(ExperimentLimits.MinFanOutCount, ExperimentLimits.MaxFanOutCount, 10),
                    ["payloadSizeBytes"] = new(ExperimentLimits.MinPayloadSizeBytes, ExperimentLimits.MaxPayloadSizeBytes, 8 * 1024),
                },
                new List<ExperimentPreset>
                {
                    new("narrow", "Narrow Fan-Out", "Few concurrent branches.", new ExperimentParameters { DurationSeconds = 5, FanOutCount = 5, PayloadSizeBytes = 4 * 1024 }),
                    new("wide", "Wide Fan-Out", "Many concurrent branches.", new ExperimentParameters { DurationSeconds = 10, FanOutCount = 30, PayloadSizeBytes = 16 * 1024 }),
                }),

            new(
                ExperimentId.TlsHandshakeStress,
                "TLS Handshake Stress",
                ExperimentCategory.Sockets,
                "Opens fresh TCP connections and performs new TLS handshakes against this service's HTTPS endpoint on every iteration.",
                "Connection rate is bounded by duration; all handshakes target this same process over loopback HTTPS.",
                duration,
                new Dictionary<string, ParameterBound>
                {
                    ["workerCount"] = new(ExperimentLimits.MinWorkerCount, Math.Min(8, ExperimentLimits.MaxWorkerCount), 1),
                },
                new List<ExperimentPreset>
                {
                    new("light", "Light", "Sequential handshakes.", new ExperimentParameters { DurationSeconds = 5, WorkerCount = 1 }),
                    new("concurrent", "Concurrent", "Multiple concurrent handshake loops.", new ExperimentParameters { DurationSeconds = 10, WorkerCount = 4 }),
                }),

            new(
                ExperimentId.GrpcUnaryEcho,
                "gRPC Unary Echo",
                ExperimentCategory.Grpc,
                "Repeatedly issues unary gRPC calls against this service's own gRPC endpoint, echoing a payload of a given size.",
                "Message count and size are capped; calls stay on loopback.",
                duration,
                new Dictionary<string, ParameterBound>
                {
                    ["messageCount"] = new(ExperimentLimits.MinMessageCount, ExperimentLimits.MaxMessageCount, 50),
                    ["messageSizeBytes"] = new(ExperimentLimits.MinMessageSizeBytes, ExperimentLimits.MaxMessageSizeBytes, 4 * 1024),
                },
                new List<ExperimentPreset>
                {
                    new("small", "Small Messages", "Many small unary calls.", new ExperimentParameters { DurationSeconds = 5, MessageCount = 100, MessageSizeBytes = 512 }),
                    new("large", "Large Messages", "Fewer, larger unary calls.", new ExperimentParameters { DurationSeconds = 10, MessageCount = 20, MessageSizeBytes = 256 * 1024 }),
                }),

            new(
                ExperimentId.GrpcServerStreaming,
                "gRPC Server Streaming",
                ExperimentCategory.Grpc,
                "Opens a server-streaming gRPC call and consumes a bounded number of streamed messages against this service's own endpoint.",
                "Stream length and message size are capped; calls stay on loopback.",
                duration,
                new Dictionary<string, ParameterBound>
                {
                    ["messageCount"] = new(ExperimentLimits.MinMessageCount, ExperimentLimits.MaxMessageCount, 50),
                    ["messageSizeBytes"] = new(ExperimentLimits.MinMessageSizeBytes, ExperimentLimits.MaxMessageSizeBytes, 4 * 1024),
                },
                new List<ExperimentPreset>
                {
                    new("short", "Short Stream", "A brief stream of small messages.", new ExperimentParameters { DurationSeconds = 5, MessageCount = 50, MessageSizeBytes = 512 }),
                    new("long", "Long Stream", "An extended stream of larger messages.", new ExperimentParameters { DurationSeconds = 10, MessageCount = 200, MessageSizeBytes = 8 * 1024 }),
                }),

            new(
                ExperimentId.CustomOtelMetricsDemo,
                "Custom OTel Metrics Demo",
                ExperimentCategory.CustomMetrics,
                "Emits a stream of synthetic samples onto the lab's custom OpenTelemetry Meter so dashboards and alerts can be exercised end to end.",
                "Purely in-process metric emission; no external calls, no resource pressure.",
                duration,
                new Dictionary<string, ParameterBound>
                {
                    ["intensityPercent"] = new(ExperimentLimits.MinIntensityPercent, ExperimentLimits.MaxIntensityPercent, 10),
                },
                new List<ExperimentPreset>
                {
                    new("steady", "Steady", "A slow, steady stream of samples.", new ExperimentParameters { DurationSeconds = 10, IntensityPercent = 5 }),
                    new("bursty", "Bursty", "A fast stream of samples.", new ExperimentParameters { DurationSeconds = 10, IntensityPercent = 80 }),
                }),
        }.ToDictionary(e => e.Id);

        Entries = _entries.Values.ToList();
    }

    public IReadOnlyList<ExperimentCatalogEntry> Entries { get; }

    public bool TryGet(ExperimentId id, out ExperimentCatalogEntry entry) => _entries.TryGetValue(id, out entry!);

    public ExperimentCatalogEntry Get(ExperimentId id) =>
        _entries.TryGetValue(id, out var entry) ? entry : throw new Coordination.ExperimentNotFoundException(id);
}
