using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using RuntimeMetricsLab.Analysis;
using RuntimeMetricsLab.Contracts.Attribution;

[SupportedOSPlatform("windows")]
internal static class WindowsTraceInspection
{
    public static int Run(string tracePath, string targetPath, string outputPath)
    {
        tracePath = Path.GetFullPath(tracePath);
        targetPath = Path.GetFullPath(targetPath);
        outputPath = Path.GetFullPath(outputPath);
        if (File.Exists(outputPath)) throw new IOException("Inspection output must be a new file.");
        if (new FileInfo(tracePath).Length > 512L * 1024 * 1024)
            throw new InvalidDataException("ETL exceeds the 512 MiB inspection limit.");
        var target = JsonSerializer.Deserialize<TargetStatus>(File.ReadAllText(targetPath)) ??
            throw new InvalidDataException("Saved target status is missing.");
        var collector = JsonSerializer.Deserialize<CollectorStatus>(
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(tracePath)!, "collector.json"))) ??
            throw new InvalidDataException("Saved collector status is missing.");
        var before = Hash(tracePath);
        var counts = new Dictionary<Shape, (long Count, double First, double Last)>();
        var transitions = new List<object>();
        var markers = new List<object>();
        var cpu = new Dictionary<int, (long Samples, long Weight, long NonProcess)>();
        var stackProcesses = new Dictionary<int, long>();
        var sampleKeys = new HashSet<(int ThreadId, long Qpc)>();
        var stackKeys = new HashSet<(int ThreadId, long Qpc)>();
        long allocations = 0, allocationObjectBytes = 0, events = 0;
        using var source = new ETWTraceEventSource(tracePath, TraceEventSourceType.FileOnly);
        // AllEvents alone does not subscribe the typed parser templates.
        source.Kernel.All += _ => { };
        source.Clr.All += _ => { };
        new ClrRundownTraceEventParser(source).All += _ => { };
        source.Dynamic.All += _ => { };
#pragma warning disable CS0618 // Correlate native CPU and StackWalk records by their actual QPC timestamps.
        source.Kernel.PerfInfoSample += data =>
        {
            var prior = cpu.GetValueOrDefault(data.ProcessID);
            cpu[data.ProcessID] = (prior.Samples + 1, prior.Weight + data.Count, prior.NonProcess + (data.NonProcess ? 1 : 0));
            sampleKeys.Add((data.ThreadID, data.TimeStampQPC));
        };
        source.Kernel.StackWalkStack += data =>
        {
            stackProcesses[data.ProcessID] = stackProcesses.GetValueOrDefault(data.ProcessID) + 1;
            stackKeys.Add((data.ThreadID, data.EventTimeStampQPC));
        };
#pragma warning restore CS0618
        source.Clr.AllocationSampling += data =>
        {
            allocations++;
            allocationObjectBytes += data.ObjectSize;
        };
        source.AllEvents += data =>
        {
            if (++events > 2_000_000) throw new InvalidDataException("ETL exceeds the two-million-event inspection limit.");
            var shape = new Shape(data.ProviderGuid, data.ProviderName, (int)data.ID, data.EventName,
                (int)data.Opcode, data.Version, data.ProcessID, data.GetType().Name);
            if (!counts.TryGetValue(shape, out var prior))
            {
                if (counts.Count >= 10_000) throw new InvalidDataException("ETL metadata shape bound exceeded.");
                prior = (0, data.TimeStampRelativeMSec, data.TimeStampRelativeMSec);
            }
            counts[shape] = (prior.Count + 1, prior.First, data.TimeStampRelativeMSec);
            if ((data.ProviderGuid == ClrRundownTraceEventParser.ProviderGuid && (int)data.ID is >= 145 and <= 148) ||
                (data.ProviderGuid == ClrTraceEventParser.ProviderGuid && (int)data.ID is 135 or 136))
            {
                if (transitions.Count >= 10_000) throw new InvalidDataException("Rundown transition bound exceeded.");
                transitions.Add(new { Shape = shape, Milliseconds = data.TimeStampRelativeMSec, Payload = Payload(data) });
            }
            if (data.ProviderName == "RuntimeMetricsLab-Attribution")
            {
                if (markers.Count >= 10_000) throw new InvalidDataException("Marker bound exceeded.");
                markers.Add(new { Shape = shape, Milliseconds = data.TimeStampRelativeMSec, Payload = Payload(data) });
            }
        };
        source.Process();
        var decoded = WindowsEtwDecoder.Inspect(tracePath, Path.ChangeExtension(outputPath, ".etlx"), target.Identity.ProcessId);
        var after = Hash(tracePath);
        if (before != after) throw new IOException("Input ETL changed while being inspected.");
        var measurement = target.Measurement;
        var window = measurement?.Window;
        using var identity = WindowsIdentity.GetCurrent();
        var result = new
        {
            Inspection = "Offline saved target-filtered ETL; no providers enabled and no capture state changed",
            Elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            TracePath = tracePath,
            TraceBytes = new FileInfo(tracePath).Length,
            TraceSha256 = before,
            InputUnchanged = before == after,
            Collector = collector,
            Target = new
            {
                target.RunId, target.Phase, target.Error, target.Identity,
                WindowComplete = window?.Complete,
                StartTicks = window?.Start.MonotonicTicks,
                EndTicks = window?.End.MonotonicTicks,
                Frequency = window?.Frequency,
                Seconds = window is null ? (double?)null :
                    (double)(window.End.MonotonicTicks - window.Start.MonotonicTicks) / window.Frequency,
                UserCpuSeconds = window is null ? null : AttributionMetrics.Delta(window, "dotnet.process.cpu.time", "cpu.mode", "user"),
                KernelCpuSeconds = window is null ? null : AttributionMetrics.Delta(window, "dotnet.process.cpu.time", "cpu.mode", "system"),
                AverageCores = window is null ? null : AttributionMetrics.IntervalCores(window),
                AllocatedBytes = window is null ? null : AttributionMetrics.Delta(window, "dotnet.gc.heap.total_allocated"),
                measurement?.IndependentCpuSeconds, measurement?.IndependentAllocatedBytes, measurement?.Calls
            },
            EventCount = events,
            source.EventsLost,
            EventShapes = counts.OrderBy(item => item.Key.Provider).ThenBy(item => item.Key.Id).ThenBy(item => item.Key.ProcessId)
                .Select(item => new { item.Key, item.Value.Count, FirstMilliseconds = item.Value.First, LastMilliseconds = item.Value.Last }).ToArray(),
            RundownTransitions = transitions,
            Markers = markers,
            Cpu = cpu.Select(item => new { ProcessId = item.Key, item.Value.Samples, item.Value.Weight, item.Value.NonProcess }).ToArray(),
            Stacks = stackProcesses.Select(item => new { ProcessId = item.Key, Count = item.Value }).ToArray(),
            CpuKeysWithStackRecord = sampleKeys.Count(stackKeys.Contains),
            Allocations = new { Samples = allocations, SampledObjectBytes = allocationObjectBytes },
            Decoded = new
            {
                CpuObservations = decoded.Cpu.Length, AllocationObservations = decoded.Allocations.Length,
                AllocationsWithResolvedFrames = decoded.Allocations.Count(item => item.Frames.Any(frame => !frame.Method.StartsWith('['))),
                AllocationsWithLabFrames = decoded.Allocations.Count(item => item.Frames.Any(frame =>
                    frame.Module.Contains("RuntimeMetricsLab", StringComparison.OrdinalIgnoreCase))),
                decoded.EventsLost, decoded.Headers, decoded.IdentityEvents,
                TopAllocationFrames = decoded.Allocations.GroupBy(item => item.Frames[0]).OrderByDescending(group => group.Count())
                    .Take(10).Select(group => new { Frame = group.Key, Samples = group.Count() }).ToArray()
            },
            Limitation = "Failed collector status is preserved. Missing final rundown and unrecorded native loss/sample interval are not fabricated; this is not a completed cost report."
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(JsonSerializer.Serialize(new { outputPath, Events = events, RundownEvents = transitions.Count, Cpu = cpu.Values.Sum(item => item.Samples), Allocations = allocations, before }));
        return 0;
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static Dictionary<string, string> Payload(TraceEvent data) => data.PayloadNames
        .Select((name, index) => (name, value: Convert.ToString(data.PayloadValue(index), CultureInfo.InvariantCulture) ?? ""))
        .ToDictionary(item => item.name, item => item.value, StringComparer.Ordinal);

    private sealed record Shape(Guid ProviderGuid, string Provider, int Id, string EventName, int Opcode, int Version,
        int ProcessId, string Type);
}
