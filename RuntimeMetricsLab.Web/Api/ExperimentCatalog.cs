namespace RuntimeMetricsLab.Web.Api;

/// <summary>Category of runtime behaviour that an experiment page focuses on.</summary>
public enum MetricCategory
{
    Cpu,
    GcAllocations,
    ThreadPool,
    Http,
    SocketsTls,
    Grpc,
    CustomOtel
}

/// <summary>Describes one OpenTelemetry instrument a category page should highlight while a run is active.</summary>
public sealed record InstrumentInfo(string Name, string Type, string Unit, string ExpectedMovement);

/// <summary>A locally-defined, intentionally bounded preset used when the API hasn't supplied its own preset catalog yet.</summary>
public sealed record SafePreset(string Id, string Name, string Description, string RiskNote);

/// <summary>Explains which process performs each part of an experiment and why its resources move.</summary>
public sealed record ExerciseFlow(
    string ClientActivity,
    string ServerActivity,
    string ResourceImpact);

/// <summary>Static, backend-independent teaching content for one metric category. Merged at runtime with whatever GET /api/experiments returns.</summary>
public sealed record CategoryInfo(
    MetricCategory Category,
    string Slug,
    string DisplayName,
    string ShortLabel,
    string Meter,
    string IconClass,
    string Overview,
    ExerciseFlow Flow,
    IReadOnlyList<InstrumentInfo> Instruments,
    IReadOnlyList<SafePreset> SafePresets,
    string DashboardGuidance,
    IReadOnlyList<string> MatchKeywords);

/// <summary>
/// Local catalog of meter/instrument/unit/expected-movement guidance for every category, plus safe
/// default presets used until the backend contract in RuntimeMetricsLab.ApiService/Experiments is
/// finalized. Content here mirrors the meters that RuntimeMetricsLab.ServiceDefaults already wires
/// into OpenTelemetry (see Extensions.cs BuiltInMeterNames + AddRuntimeInstrumentation), so it stays
/// accurate even for categories the API doesn't support yet.
/// </summary>
public static class ExperimentCatalog
{
    public static readonly IReadOnlyList<CategoryInfo> Categories = new List<CategoryInfo>
    {
        new(
            MetricCategory.Cpu,
            "cpu",
            "CPU",
            "CPU time & core usage",
            "System.Runtime",
            "bi-cpu",
            "CPU experiments generate bounded, time-limited compute work on the web host's process so you can watch processor-time counters move in near real time.",
            new(
                "The browser sends a Blazor event to the client resource. The client sends one start command to its paired server, then polls for status. Apart from this small amount of UI and HTTP work, it does not perform the CPU burn.",
                "The server creates the bounded number of worker tasks selected by the preset. Each worker alternates compute-heavy busy loops with short waits until the duration expires or the run is cancelled.",
                "Expect the server's CPU-time rate to rise in proportion to worker count and intensity. The client should remain near baseline, with only brief Blazor and HTTP activity. Working set may increase slightly from additional active worker stacks."),
            new List<InstrumentInfo>
            {
                new("dotnet.process.cpu.time", "Counter<double>", "s (seconds)", "Increases monotonically while the process uses CPU; the 'user' mode rate rises sharply during a CPU burst preset."),
                new("dotnet.process.cpu.count", "UpDownCounter<int>", "{cpu}", "Stays flat — it reports the number of cores available to the process (Environment.ProcessorCount), not utilization."),
                new("dotnet.process.memory.working_set", "UpDownCounter<long>", "By (bytes)", "May rise slightly during multi-core presets as more thread stacks and buffers are touched.")
            },
            new List<SafePreset>
            {
                new("cpu-idle-baseline", "Idle baseline", "No synthetic load — lets you see the resting CPU time rate for comparison.", "None; this is the safe default."),
                new("cpu-light-burst", "Light single-core burst", "Runs a short, bounded spin/compute loop on one core for a few seconds.", "Brief single-core spike; safe on any host."),
                new("cpu-parallel-bounded", "Bounded multi-core burst", "Runs the same compute loop across a capped number of cores (never more than Environment.ProcessorCount) for a few seconds.", "Short multi-core spike; capped duration and core count keep it safe for shared hosts.")
            },
            "Watch process.cpu.time by cpu.mode in the Aspire dashboard's Metrics view (or your OTLP backend such as Prometheus/Grafana) filtered to the 'webfrontend' or 'apiservice' resource — whichever the run's target selects.",
            new[] { "cpu", "processor", "compute" }),

        new(
            MetricCategory.GcAllocations,
            "gc",
            "GC & Allocations",
            "Garbage collection & heap",
            "System.Runtime",
            "bi-recycle",
            "GC experiments allocate managed objects in controlled batches so you can observe collection counts, heap size, and pause time move together.",
            new(
                "The client sends the start request and polls the run state. It does not allocate the experiment buffers, so its allocation and GC metrics should change only slightly from rendering and polling.",
                "The server repeatedly allocates byte arrays at the preset size and rate, touches them so the memory is committed, and releases the references. The objects become eligible for collection instead of being retained indefinitely.",
                "Expect allocation totals and mostly Gen0 collections to rise on the server. Larger buffers or faster rates can also increase Gen1/Gen2 activity, GC pause time, and temporary heap/working-set size. After the run, live heap should move back toward baseline even though total allocated bytes never decrease."),
            new List<InstrumentInfo>
            {
                new("dotnet.gc.collections", "Counter<int>", "{collection}", "Increases per generation (gen0/gen1/gen2) as allocations trigger collections; small-object churn mostly drives gen0."),
                new("dotnet.gc.heap.total_allocated", "Counter<long>", "By (bytes)", "Climbs continuously and never decreases — it's a running total of bytes ever allocated on the managed heap."),
                new("dotnet.gc.last_collection.heap.size", "UpDownCounter<long>", "By (bytes)", "Rises while live objects accumulate and drops after a collection reclaims dead objects."),
                new("dotnet.gc.pause.time", "Counter<double>", "s (seconds)", "Increases in small steps each time the GC briefly pauses managed threads to collect.")
            },
            new List<SafePreset>
            {
                new("gc-small-object-churn", "Small object churn", "Allocates and releases many short-lived small objects in bounded batches — mostly gen0 pressure.", "Low risk; objects are released quickly so heap growth is bounded."),
                new("gc-large-array", "Bounded large array allocation", "Allocates a capped number of moderately large arrays, then lets them go out of scope.", "Capped total bytes to avoid large-object-heap pressure on shared hosts."),
                new("gc-gen2-pressure", "Gen2 pressure (bounded)", "Retains a small, capped number of longer-lived objects to nudge collections toward gen2.", "Retained objects are capped and released at the end of the run.")
            },
            "In the Aspire dashboard's Metrics view, chart dotnet.gc.collections split by gc.heap.generation alongside dotnet.gc.heap.total_allocated for the resource the run targeted.",
            new[] { "gc", "alloc", "heap", "garbage" }),

        new(
            MetricCategory.ThreadPool,
            "threadpool",
            "Thread Pool",
            "Thread pool saturation",
            "System.Runtime",
            "bi-diagram-3",
            "Thread pool experiments queue bounded numbers of work items so you can see thread count, queue length, and completed work items respond to load.",
            new(
                "The client starts the run and polls it asynchronously. Its Blazor circuit should stay responsive because the blocking work is not executed in the client process.",
                "The server queues a capped number of work items that synchronously occupy thread-pool workers for the configured duration. Additional queued work must wait until workers become available or the pool grows.",
                "Expect the server's thread count and queue length to rise, with completed-work count advancing as items finish. Request latency can increase while workers are occupied. The queue should drain after cancellation or completion; the client should show only normal polling activity."),
            new List<InstrumentInfo>
            {
                new("dotnet.thread_pool.thread.count", "UpDownCounter<int>", "{thread}", "Rises as the pool grows worker threads to keep up with queued work, then settles back down after the run."),
                new("dotnet.thread_pool.queue.length", "UpDownCounter<int>", "{work_item}", "Spikes when work items are enqueued faster than they can run, then drains toward zero."),
                new("dotnet.thread_pool.work_item.count", "Counter<long>", "{work_item}", "Increases monotonically as queued items finish executing."),
                new("dotnet.monitor.lock_contentions", "Counter<long>", "{contention}", "May tick up during the blocking-work preset if simulated work contends on a shared lock.")
            },
            new List<SafePreset>
            {
                new("tp-burst-short-tasks", "Burst of short tasks", "Queues a capped number of very short, non-blocking tasks all at once.", "Low risk; each task is short and the total count is capped."),
                new("tp-bounded-blocking", "Bounded blocking work", "Queues a small, capped number of tasks that briefly block a worker thread (e.g. Thread.Sleep) to illustrate pool growth.", "Bounded duration and count avoid starving the pool for long."),
                new("tp-async-io-bound", "Simulated async I/O-bound work", "Queues tasks that asynchronously await a short delay, showing the pool stays small because threads are freed while awaiting.", "Non-blocking by design; safest of the three presets.")
            },
            "Compare dotnet.thread_pool.queue.length against dotnet.thread_pool.thread.count in the dashboard — a persistently high queue with a flat thread count suggests saturation, which the async preset is designed to avoid.",
            new[] { "thread", "pool", "queue" }),

        new(
            MetricCategory.Http,
            "http",
            "HTTP",
            "Outbound HTTP calls",
            "System.Net.Http",
            "bi-hdd-network",
            "HTTP experiments make bounded outbound calls (via the resilient typed HttpClient already wired up by RuntimeMetricsLab.ServiceDefaults) so you can observe request duration, active requests, and connection reuse.",
            new(
                "The Blazor client sends the experiment-control request to its paired server and polls status. It is not the source of the payload, delay, failure, or fan-out traffic in the current lab design.",
                "The server becomes both HTTP client and HTTP server: the workload uses HttpClient to call bounded loopback endpoints in the same server process. Depending on the selected exercise, those endpoints return payloads, delay responses, inject 5xx failures, or serve concurrent fan-out branches.",
                "On the server, expect outbound http.client.* and inbound http.server.* instruments to move together. Fan-out raises active requests and may open more pooled connections; delay shifts duration upward; payloads increase transfer work; failure injection adds error/status dimensions. The client mainly shows the single control request and polling."),
            new List<InstrumentInfo>
            {
                new("http.client.request.duration", "Histogram<double>", "s (seconds)", "Records one sample per completed request; the distribution widens under the concurrent-burst preset."),
                new("http.client.active_requests", "UpDownCounter<long>", "{request}", "Rises while requests are in flight and falls back to zero once they complete."),
                new("http.client.open_connections", "UpDownCounter<long>", "{connection}", "Grows as new pooled connections are opened for concurrent requests, then idles down after reuse.")
            },
            new List<SafePreset>
            {
                new("http-sequential-get", "Sequential GET calls", "Issues a capped number of GET requests to a sample endpoint one at a time.", "Low load; one request in flight at any time."),
                new("http-concurrent-burst", "Concurrent GET burst (bounded)", "Issues a capped number of GET requests with limited concurrency to exercise connection pooling.", "Concurrency and total request count are both capped."),
                new("http-slow-upstream", "Simulated slow upstream", "Calls an endpoint with an artificial delay to demonstrate timeout/resilience handling from AddStandardResilienceHandler.", "Bounded delay and a single request avoid long-running load.")
            },
            "Look at http.client.request.duration (as a histogram/heatmap) and http.client.active_requests for the 'webfrontend' resource; RuntimeMetricsLab.ServiceDefaults already adds HttpClient instrumentation for every typed client.",
            new[] { "http", "outbound", "request" }),

        new(
            MetricCategory.SocketsTls,
            "sockets-tls",
            "Sockets & TLS",
            "Connections & handshakes",
            "System.Net.NameResolution / System.Net.Http",
            "bi-shield-lock",
            "Sockets/TLS experiments establish bounded numbers of new connections (rather than reusing pooled ones) so DNS lookup and connection/TLS handshake timings are visible.",
            new(
                "The client only starts and monitors the run through its normal reusable HttpClient connection to the paired server.",
                "The server workload creates fresh HTTP handlers/clients and repeatedly connects to its own HTTPS endpoint. Disabling reuse forces new TCP connections and TLS handshakes; worker count controls how many handshake loops run concurrently.",
                "Expect connection-duration, TLS handshake, active-connection, and Kestrel connection metrics to move on the server. Fresh connections produce many more samples than pooled reuse. DNS may remain quiet for loopback or cached names. The client resource should not show the handshake storm."),
            new List<InstrumentInfo>
            {
                new("dns.lookup.duration", "Histogram<double>", "s (seconds)", "Records one sample per DNS resolution; a preset that forces fresh connections will show a sample per run."),
                new("http.client.connection.duration", "Histogram<double>", "s (seconds)", "Captures TCP connect time and, for HTTPS targets, the TLS handshake — expect a visible sample each time a new (non-pooled) connection is opened."),
                new("http.client.open_connections", "UpDownCounter<long>", "{connection}", "Reflects how many connections (active + idle) are currently pooled for the target.")
            },
            new List<SafePreset>
            {
                new("net-fresh-connection", "Fresh TLS connection per call", "Disables connection reuse for a capped number of calls so each one performs a full DNS + TCP + TLS handshake.", "Small capped call count; only affects this experiment's HttpClient instance."),
                new("net-pooled-reuse", "Pooled connection reuse", "Makes the same number of calls while allowing normal pooling, for a side-by-side comparison of handshake overhead.", "Identical safe request pattern to the fresh-connection preset, just with reuse enabled."),
                new("net-large-payload", "Bounded larger payload transfer", "Transfers a capped, moderate-size payload over a single connection to show socket throughput without handshake noise.", "Payload size is capped to keep the transfer short.")
            },
            "Compare dns.lookup.duration and http.client.connection.duration between the two presets in the dashboard — pooled reuse should show far fewer (ideally zero) new-connection samples after the first call.",
            new[] { "socket", "tls", "ssl", "handshake", "dns" }),

        new(
            MetricCategory.Grpc,
            "grpc",
            "gRPC",
            "RPC calls",
            "Grpc.AspNetCore.Server",
            "bi-broadcast-pin",
            "gRPC experiments make bounded unary or streaming calls against a sample gRPC service so you can watch server-side call duration and concurrency.",
            new(
                "The client sends the REST control command and polls status; it does not directly open the demonstration gRPC stream.",
                "Inside the server, a gRPC client calls the gRPC service hosted by that same server. Unary runs send request/response messages repeatedly; streaming runs keep a call open while the server emits a bounded sequence of messages.",
                "Because both sides live in the server process, expect gRPC client activity, gRPC server duration/active-call metrics, HTTP/2 connection activity, and serialization allocations there. Streaming should hold active calls above zero longer; larger messages should add allocations and network bytes. The Blazor client sees only control traffic."),
            new List<InstrumentInfo>
            {
                new("grpc.server.call.duration", "Histogram<double>", "s (seconds)", "Records one sample per completed RPC, tagged with grpc.method/grpc.service/status — deadline-exceeded presets show up as failed-status samples."),
                new("grpc.server.active_calls", "UpDownCounter<long>", "{call}", "Rises while calls (especially streaming ones) are in progress and falls back to zero once they complete.")
            },
            new List<SafePreset>
            {
                new("grpc-unary-burst", "Unary call burst (bounded)", "Issues a capped number of simple unary RPCs in sequence.", "Low load; requests are sequential and capped."),
                new("grpc-server-streaming", "Bounded server-streaming sample", "Opens a single server-streaming call that emits a capped number of messages then completes.", "Message count and duration are both capped."),
                new("grpc-deadline-demo", "Deadline / cancellation demo", "Issues a call with a short deadline so you can see a deliberate deadline-exceeded status.", "Single call, short deadline — designed to fail fast and safely.")
            },
            "In the dashboard, filter grpc.server.call.duration by grpc.status to separate the deadline-demo preset's expected failures from successful calls in the other presets.",
            new[] { "grpc", "rpc" }),

        new(
            MetricCategory.CustomOtel,
            "custom-metrics",
            "Custom OpenTelemetry Metrics",
            "App-defined instruments",
            "RuntimeMetricsLab.Experiments",
            "bi-graph-up-arrow",
            "This category exercises the lab's own Meter (already registered by RuntimeMetricsLab.ServiceDefaults as \"RuntimeMetricsLab.Experiments\") so you can see how a Counter, Histogram, and UpDownCounter you define yourself behave end to end.",
            new(
                "The client starts, polls, and optionally cancels the run. Its actions create the same control-plane traffic as every other exercise.",
                "The server emits bounded synthetic measurements through the RuntimeMetricsLab.Experiments Meter. It increments counters, records histogram samples, and changes active-run up/down values without intentionally creating meaningful CPU, memory, or network pressure.",
                "Expect the custom instruments on the server to change exactly according to the run lifecycle and sample rate. Built-in resource metrics should remain close to baseline, which demonstrates that application metrics can describe logical work even when physical resource usage barely changes."),
            new List<InstrumentInfo>
            {
                new("experiments.runs.started", "Counter<long>", "{run}", "Increments by exactly one each time you start a run from this UI, regardless of category."),
                new("experiments.run.duration", "Histogram<double>", "s (seconds)", "Records one sample when a run finishes, so its distribution grows one bucket at a time as you run more experiments."),
                new("experiments.active.count", "UpDownCounter<int>", "{run}", "Rises by one when a run starts and falls by one when it finishes, cancels, or fails — a live 'how many runs are active right now' gauge.")
            },
            new List<SafePreset>
            {
                new("otel-counter-increment", "Emit counter increments", "Emits a capped number of increments to a demo Counter instrument.", "Purely additive counter increments; no side effects."),
                new("otel-histogram-sample", "Emit histogram distribution sample", "Emits a capped number of randomized-but-bounded samples into a demo Histogram instrument.", "Sample values are generated within a fixed, safe range."),
                new("otel-updowncounter-oscillate", "Emit up-down counter oscillation", "Increments then decrements a demo UpDownCounter a capped number of times to show it return to baseline.", "Guaranteed to return to its starting value at the end of the run.")
            },
            "Add the RuntimeMetricsLab.Experiments meter as a custom metric source in your OTLP backend (it's already included in ConfigureOpenTelemetry) and watch experiments.active.count move live as you start/stop runs here.",
            new[] { "otel", "custom", "experiments" })
    };

    public static CategoryInfo Get(MetricCategory category) =>
        Categories.First(c => c.Category == category);

    /// <summary>
    /// Best-effort match between a backend-reported experiment and one of the local categories,
    /// based on simple keyword matching against the experiment's id/name/category fields. This is
    /// intentionally loose so the UI keeps working if the backend's naming differs from this catalog.
    /// </summary>
    public static bool Matches(CategoryInfo category, ExperimentSummary experiment)
    {
        var haystack = string.Join(' ', experiment.Id, experiment.Name, experiment.Category ?? "").ToLowerInvariant();
        return category.MatchKeywords.Any(keyword => haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
