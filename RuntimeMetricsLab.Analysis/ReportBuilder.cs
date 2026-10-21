using System.Globalization;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Analysis;

public static class ReportBuilder
{
    public static AttributionReport Build(CaptureBundle bundle, DecodedTrace trace)
    {
        var target = bundle.Target;
        var window = target.Window;
        var seconds = (window.End.MonotonicTicks - window.Start.MonotonicTicks) / (double)window.Frequency;
        if (bundle.SchemaVersion != 1 || target.RunId != bundle.JobId || bundle.Collector.OperationId != bundle.JobId ||
            window.Frequency <= 0 || !double.IsFinite(seconds) || seconds <= 0)
            throw new InvalidDataException("Bundle identity, schema, or monotonic measurement window is invalid.");
        var quality = new List<QualityNotice>();
        if (bundle.ArtifactHashes is null)
            quality.Add(new("legacy-counter-checksum", "warning", "This early bundle predates separate counter-artifact checksums. Its trace checksum is verified; new captures also hash target counters and collector diagnostics."));
        var markers = trace.Markers.Where(marker => marker.Payload.GetValueOrDefault("runId") == target.RunId.ToString("N")).ToArray();
        var starts = markers.Where(marker => marker.Payload.GetValueOrDefault("phase") == "measure-start").ToArray();
        var ends = markers.Where(marker => marker.Payload.GetValueOrDefault("phase") == "measure-end").ToArray();
        if (starts.Length != 1 || ends.Length != 1)
            throw new InvalidDataException("Missing or duplicate run-nonce measurement boundaries; attribution is unsupported.");
        var start = starts[0];
        var end = ends[0];
        if (start.ProcessId != target.Identity.ProcessId || end.ProcessId != target.Identity.ProcessId ||
            start.Payload.GetValueOrDefault("monotonicTicks") != window.Start.MonotonicTicks.ToString(CultureInfo.InvariantCulture) ||
            end.Payload.GetValueOrDefault("monotonicTicks") != window.End.MonotonicTicks.ToString(CultureInfo.InvariantCulture) ||
            end.Milliseconds <= start.Milliseconds ||
            Math.Abs((end.Milliseconds - start.Milliseconds) / 1000 - seconds) > 0.05)
            throw new InvalidDataException("Trace markers do not match the target's window and process identity.");
        var provenance = AttributionModes.Provenance(bundle.Collector);
        if (provenance.Platform != target.Identity.Platform)
            throw new InvalidDataException("Collector and target platform provenance do not match.");
        if (provenance.Platform == AttributionPlatform.LinuxContainer)
        {
            if (!trace.IdentityEvents.Any(item => item.EventName == "Universal.System/ExistingProcess" &&
                item.ProcessId == target.Identity.ProcessId && item.Payload.GetValueOrDefault("NamespaceId") == target.Identity.ProcessId.ToString(CultureInfo.InvariantCulture)))
                throw new InvalidDataException("Universal process/namespace identity mapping is missing.");
        }
        else
        {
            var validRawScope = provenance.Format == AttributionTraceFormat.WindowsEtw && provenance.TargetOnlyArtifact ||
                provenance.Format == AttributionTraceFormat.PerfViewEtw && !provenance.TargetOnlyArtifact;
            if (!provenance.SystemCpuReceptionConsented || !validRawScope ||
                bundle.Collector.TargetProcessId != target.Identity.ProcessId ||
                bundle.Collector.TargetProcessStartUtc != target.Identity.ProcessStartUtc ||
                target.Identity.ProcessStartUtc is null ||
                !trace.IdentityEvents.Any(item => item.ProcessId == target.Identity.ProcessId &&
                    item.Payload.GetValueOrDefault("runId") == target.RunId.ToString("N") &&
                    item.Payload.GetValueOrDefault("instanceId") == target.Identity.InstanceId.ToString("N") &&
                    DateTimeOffset.TryParse(item.Payload.GetValueOrDefault("processStartUtc"), CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var markerStart) && markerStart == target.Identity.ProcessStartUtc.Value &&
                    item.Payload.GetValueOrDefault("moduleMvid") == target.Identity.Environment.GetValueOrDefault("workload-module-mvid")))
                throw new InvalidDataException("Windows capture identity, consent, or declared raw-trace scope is missing.");
        }
        if (trace.Cpu.Any(sample => sample.ProcessId != target.Identity.ProcessId) ||
            trace.Allocations.Any(sample => sample.ProcessId != target.Identity.ProcessId))
            throw new InvalidDataException("Trace contains observations from a process other than the fixed target.");

        var cpu = trace.Cpu.Where(sample => sample.Milliseconds >= start.Milliseconds && sample.Milliseconds < end.Milliseconds).ToArray();
        var allocations = trace.Allocations.Where(sample => sample.Milliseconds >= start.Milliseconds && sample.Milliseconds < end.Milliseconds).ToArray();
        var cpuSeconds = AttributionMetrics.Delta(window, "dotnet.process.cpu.time", "cpu.mode", "user") +
            AttributionMetrics.Delta(window, "dotnet.process.cpu.time", "cpu.mode", "system");
        var allocatedBytes = AttributionMetrics.Delta(window, "dotnet.gc.heap.total_allocated");
        var cores = AttributionMath.AverageCores(cpuSeconds, seconds);
        var totalWeight = cpu.Sum(sample => (double)sample.Weight);
        double? nominal = trace.Headers.TryGetValue("ExpectedCPUSamplingRate", out var rawInterval) &&
            double.TryParse(rawInterval, CultureInfo.InvariantCulture, out var nanoseconds) && nanoseconds > 0 && double.IsFinite(nanoseconds)
            ? totalWeight * nanoseconds / 1_000_000_000 : null;
        var sampledBytes = allocations.Sum(sample => AllocationEstimator.EstimatedBytes(sample.ObjectSize));
        var cpuRatio = cpuSeconds > 0 ? nominal / cpuSeconds : null;
        double? allocationRatio = allocatedBytes > 0 ? sampledBytes / allocatedBytes : null;
        var complete = window.Complete && bundle.Collector.Phase == "complete" && trace.EventsLost == 0 &&
            provenance.NativeEventsLost is not > 0 && provenance.NativeBuffersLost is not > 0 &&
            markers.Any(marker => marker.Payload.GetValueOrDefault("phase") == "attached" && marker.Milliseconds < start.Milliseconds);
        var cpuUsable = complete && cpuSeconds.HasValue && nominal.HasValue && totalWeight > 0;
        var coveredCpu = cpuUsable ? cpuSeconds : null;
        var allFrames = trace.Cpu.SelectMany(sample => sample.Frames).Concat(trace.Allocations.SelectMany(sample => sample.Frames)).ToArray();
        var moduleEvidence = allFrames.Where(frame => frame.Module.Length > 0 && !frame.Method.StartsWith('['))
            .GroupBy(frame => frame.Method, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(frame => frame.Module).Distinct(StringComparer.Ordinal).Take(2).ToArray(), StringComparer.Ordinal);
        var ambiguousSignatures = allFrames.Where(frame => frame.Module.Length == 0 && !frame.Method.StartsWith('[') &&
            (!moduleEvidence.TryGetValue(frame.Method, out var modules) || modules.Length != 1))
            .Select(frame => frame.Method).ToHashSet(StringComparer.Ordinal);
        var reconciledFrames = 0;
        var flat = new Dictionary<string, MutableMethod>(StringComparer.Ordinal);
        var root = new MutableNode("root", "", "Measured process");
        var nodeCount = 1;

        foreach (var sample in cpu)
            Add(sample.Frames.Select(ResolveModule).ToArray(), sample.Weight, 0, false);
        foreach (var sample in allocations)
            Add(AllocationFrames(sample.Frames.Select(ResolveModule).ToArray()), 0, AllocationEstimator.EstimatedBytes(sample.ObjectSize), true);
        foreach (var calls in target.Calls.Where(calls => calls.Entered > 0 || calls.Completed > 0))
        {
            if (flat.Values.Any(method => LabMethodName(method.Frame) == calls.Method)) continue;
            var key = "instrumented:" + calls.Method;
            flat[key] = new(key, new("runtimemetricslab.experiments", calls.Method + " [instrumented; no samples]"))
                { InstrumentedName = calls.Method };
        }

        var methods = flat.Values.Select(method =>
        {
            var name = method.InstrumentedName ?? LabMethodName(method.Frame);
            var calls = name is null ? null : target.Calls.SingleOrDefault(item => item.Method == name);
            var aligned = complete && calls is { Completed: > 0 } && calls.Entered == calls.Completed &&
                !ambiguousSignatures.Contains(method.Frame.Method);
            var self = method.SelfCpu > 0 ? AttributionMath.CpuMilliseconds(coveredCpu, method.SelfCpu, totalWeight) : null;
            var inclusive = method.InclusiveCpu > 0 ? AttributionMath.CpuMilliseconds(coveredCpu, method.InclusiveCpu, totalWeight) : null;
            var cpuPerCall = method.SelfCpu >= 20 && cpuRatio is >= 0.75 and <= 1.25
                ? AttributionMath.PerCall(self, calls?.Entered, calls?.Completed, aligned) : null;
            var bytesPerCall = method.AllocationSamples >= 20 && allocationRatio is >= 0.75 and <= 1.25
                ? AttributionMath.PerCall(method.SelfBytes, calls?.Entered, calls?.Completed, aligned) : null;
            return new MethodCosts(method.Key, method.Frame.Module, method.Frame.Method, name is not null ? "lab" : Category(method.Frame),
                method.SelfCpu, method.AllocationSamples, self, inclusive, self / (1000 * seconds),
                inclusive / (1000 * seconds), cpuSeconds > 0 ? self / (1000 * cpuSeconds) : null,
                method.SelfBytes, method.InclusiveBytes, method.SelfBytes / seconds, aligned ? calls?.Completed : null,
                cpuPerCall, bytesPerCall, calls?.SameThreadAllocatedBytes);
        }).OrderByDescending(method => method.SelfAverageCores ?? 0).ThenByDescending(method => method.AllocatedBytes).ToArray();

        if (!complete)
            quality.Add(new("incomplete", "error", "Capture, window, provider handshake, or event-loss evidence is incomplete. CPU totals remain measured; absolute method CPU and normal per-call costs are withheld."));
        if (!cpuSeconds.HasValue || !allocatedBytes.HasValue)
            quality.Add(new("missing-counter", "error", "A required cumulative runtime counter is unavailable or reset. Missing values are not replaced with zero or fallback measurements."));
        if (trace.EventsLost > 0)
            quality.Add(new("event-loss", "error", $"{trace.EventsLost} events were reported lost."));
        if (provenance.NativeEventsLost is > 0 || provenance.NativeBuffersLost is > 0)
            quality.Add(new("native-etw-loss", "error", $"ETW controller reported {provenance.NativeEventsLost} lost events and {provenance.NativeBuffersLost} lost buffers. Absolute method CPU/per-call estimates are withheld."));
        if (provenance.Platform == AttributionPlatform.WindowsProcess)
            quality.Add(new("windows-collection-scope", "warning", provenance.Format == AttributionTraceFormat.PerfViewEtw ?
                "Stock Microsoft PerfView persists system-wide kernel CPU/process/thread/image events in the private raw ETL. FocusProcess limits non-kernel events only. This report selects the fixed target PID, process start, build and run-window markers; it contains no other process's methods." :
                "This legacy Windows capture used the retired target-only relogger. It is not a stock PerfView capture."));
        if (bundle.Collector.KernelLostEvents is null && provenance.Platform == AttributionPlatform.LinuxContainer)
            quality.Add(new("kernel-loss-not-reported", "warning", "The verified backend does not provide reliable kernel PERF_RECORD_LOST counts in this nettrace. Zero parser-reported loss is not proof of zero kernel collection loss. Independent calibration and raw sample evidence remain visible."));
        if (provenance.Format == AttributionTraceFormat.PerfViewEtw && provenance.NativeBuffersLost is null)
            quality.Add(new("perfview-loss-evidence", "info", "Loss is read from the completed stock PerfView ETL/ETLX and its preserved log; no fabricated separate native-controller counter is added. Any reported loss prevents a ready report."));
        if (cpu.Length < 200)
            quality.Add(new("sparse-cpu", "warning", $"{cpu.Length} on-CPU samples in the measurement window. Small methods may have no samples; this is not proof of zero cost."));
        if (cpuRatio is null or < 0.75 or > 1.25)
            quality.Add(new("cpu-calibration", "warning", "Independent nominal sampled CPU differs substantially from the runtime CPU counter, or the sample interval is unavailable. Normal CPU per-call estimates are withheld."));
        if (allocationRatio is null or < 0.75 or > 1.25)
            quality.Add(new("allocation-calibration", "warning", "Independent weighted allocation differs substantially from the approximate process allocation counter, or that counter is missing. Allocation per-call estimates are withheld."));
        if (methods.Any(method => method.Method.Contains("[unresolved") || method.Method.Contains("[unknown")))
            quality.Add(new("unresolved-symbols", "warning", "Unresolved native/runtime/kernel frames are retained as visible buckets. They are not redistributed to named lab methods."));
        if (reconciledFrames > 0)
            quality.Add(new("module-metadata-reconciled", "info", $"{reconciledFrames} frame occurrences lacked a module but had an exact signature resolving to exactly one module elsewhere in this target capture. These tiered-JIT identities were joined; ambiguous signatures are not joined."));
        if (ambiguousSignatures.Count > 0)
            quality.Add(new("ambiguous-module", "warning", "Some resolved signatures have no unambiguous module evidence. Their costs remain visible and affected full-method per-call estimates are withheld."));
        quality.Add(new("sampling-estimates", "info", "Method costs are counter-calibrated on-CPU sampling estimates. Allocation is independently estimated from randomized allocation events. The 25% per-call quality guard is not a statistical confidence interval."));
        quality.Add(new("memory-scope", "info", $"Controlled retained payload: {target.RetainedPayloadBytes} bytes. Managed heap, committed memory, and working set are process-level observations; generic per-method retained/native/RSS ownership is not attributable."));
        var process = new ProcessCosts(seconds, cpuSeconds, cores, cores * 100,
            target.Identity.ProcessorCount > 0 ? cores * 100 / target.Identity.ProcessorCount : null,
            allocatedBytes, allocatedBytes / seconds, nominal, cpuRatio, allocationRatio, cpuUsable ? 0 : cores, coveredCpu);
        var explanations = new[]
        {
            $"Measured window: {seconds.ToString("F3", CultureInfo.InvariantCulture)} monotonic seconds. Process averaged {Format(cores)} CPU cores ({Format(cores * 100)}% of one core; {Format(process.PercentOfRuntimeCapacity)}% of {target.Identity.ProcessorCount} runtime-visible CPUs).",
            $"Independent pre-scaling evidence: {Format(nominal)} nominal sampled CPU seconds versus {Format(cpuSeconds)} runtime-counter CPU seconds. Weighted allocation: {sampledBytes.ToString("F0", CultureInfo.InvariantCulture)} bytes versus {Format(allocatedBytes)} approximate counter bytes. Residuals are not forced to zero.",
            "Method self cores = covered process CPU seconds x method self sample share / the same elapsed seconds. Fractional and multicore values are valid; they do not represent permanently owned physical cores.",
            "Inclusive CPU/allocated bytes include descendants and overlap. Only exclusive costs partition the covered pool. Recursion is counted once per method in flat inclusive totals.",
            "Per-call fields require measured, completed, window-aligned named lab invocations and sufficient independent sample evidence. Framework/native methods never receive invented invocation counts.",
            "Flame widths show aggregate cost, not chronological time. CPU and allocation use separate weights. Same-thread allocation calibration includes descendants; requested payload bytes exclude object headers.",
            "Last-GC heap/fragmentation/committed values describe the last collection, not instantaneous memory ownership. RSS can stay high after release; any explicit GC is labeled outside the measured work."
        };
        var adapter = provenance.Format == AttributionTraceFormat.PerfViewEtw ? "TraceEvent-3.2.8/PerfView-3.2.8/v1" :
            provenance.Format == AttributionTraceFormat.WindowsEtw ? "TraceEvent-3.2.8/Windows-ETW/v1" :
            "TraceEvent-3.2.8/OneCollect-0.1.35021/v2";
        return new(1, bundle.JobId, adapter, window, target.Identity, process,
            methods, root.Freeze(), target.Samples, quality.ToArray(), explanations, trace.EventsLost, trace.Headers, bundle.Collector);

        ProfileFrame ResolveModule(ProfileFrame frame)
        {
            if (frame.Module.Length == 0 && !frame.Method.StartsWith('[') &&
                moduleEvidence.TryGetValue(frame.Method, out var modules) && modules.Length == 1)
            {
                reconciledFrames++;
                return frame with { Module = modules[0] };
            }
            return frame;
        }

        void Add(ProfileFrame[] input, double weight, double bytes, bool allocation)
        {
            var frames = input.Select(Normalize).ToArray();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var node = root;
            node.Cpu += weight;
            node.Bytes += bytes;
            for (var index = frames.Length - 1; index >= 0; index--)
            {
                var frame = frames[index];
                var key = frame.Module + "!" + frame.Method;
                if (!flat.TryGetValue(key, out var method))
                    flat[key] = method = new(key, frame);
                if (visited.Add(key)) { method.InclusiveCpu += weight; method.InclusiveBytes += bytes; }
                if (index == 0)
                {
                    method.SelfCpu += weight;
                    method.SelfBytes += bytes;
                    if (allocation) method.AllocationSamples++;
                }
                if (!node.Children.TryGetValue(key, out var child))
                {
                    if (++nodeCount > 30_000)
                        throw new InvalidDataException("Call graph exceeds 30000 nodes; it is not silently truncated.");
                    node.Children[key] = child = new("n" + nodeCount, key, frame.Method);
                }
                node = child;
                node.Cpu += weight;
                node.Bytes += bytes;
            }
            node.SelfCpu += weight;
            node.SelfBytes += bytes;
        }
    }

    private static string Format(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "unavailable";
    private static ProfileFrame Normalize(ProfileFrame frame) => frame.Method.StartsWith("[unresolved", StringComparison.Ordinal)
        ? frame with { Method = "[unresolved " + (string.IsNullOrEmpty(frame.Module) ? "module" : frame.Module) + "]" } : frame;

    private static ProfileFrame[] AllocationFrames(ProfileFrame[] frames)
    {
        // perf user_events stacks begin inside the kernel write path and CLR event emission.
        // Allocation ownership starts at the first managed frame, not at writev/user_events.
        var index = Array.FindIndex(frames, frame => frame.Module != "vmlinux" &&
            frame.Module is not ("dotnet" or "ntdll" or "kernelbase" or "kernel32" or "coreclr" or "ntoskrnl") &&
            !frame.Module.Contains(".so", StringComparison.Ordinal));
        return index < 0 ? [new("", "[unknown allocation site]")] : frames[index..];
    }

    private static string? LabMethodName(ProfileFrame frame)
    {
        if (!frame.Module.Equals("runtimemetricslab.experiments", StringComparison.OrdinalIgnoreCase))
            return null;
        return new[] { "Arithmetic", "Checksum", "AllocateSmall", "AllocateLarge", "RetainPayload", "WaitStep", "MixedPipeline" }
            .SingleOrDefault(name => frame.Method.Contains($"AttributionLessons::{name}(", StringComparison.Ordinal) ||
                frame.Method.Contains($"AttributionLessons.{name}(", StringComparison.Ordinal));
    }

    private static string Category(ProfileFrame frame) => LabMethodName(frame) is not null ? "lab" :
        frame.Method.StartsWith("[unknown", StringComparison.Ordinal) ? "unknown" :
        frame.Module == "vmlinux" ? "kernel" :
        frame.Module == "libclrjit.so" ? "JIT" :
        frame.Module == "libcoreclr.so" && (frame.Method.StartsWith("WKS::gc_heap", StringComparison.Ordinal) ||
            frame.Method.StartsWith("SVR::gc_heap", StringComparison.Ordinal)) ? "GC" :
        frame.Module == "libcoreclr.so" ? "runtime/native" :
        frame.Module == "dotnet" || frame.Module.Contains(".so", StringComparison.Ordinal) ? "native" : "runtime/application";

    private sealed class MutableMethod(string key, ProfileFrame frame)
    {
        public string Key = key;
        public ProfileFrame Frame = frame;
        public string? InstrumentedName;
        public double SelfCpu, InclusiveCpu, SelfBytes, InclusiveBytes;
        public long AllocationSamples;
    }
    private sealed class MutableNode(string id, string key, string name)
    {
        public double Cpu, SelfCpu, Bytes, SelfBytes;
        public Dictionary<string, MutableNode> Children { get; } = new(StringComparer.Ordinal);
        public CallNode Freeze() => new(id, key, name, Cpu, SelfCpu, Bytes, SelfBytes,
            Children.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value.Freeze()).ToArray());
    }
}
