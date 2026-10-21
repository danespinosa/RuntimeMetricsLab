using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Analysis;

public static class WindowsEtwDecoder
{
    [SupportedOSPlatform("windows")]
    public static DecodedTrace Decode(string path, string indexPath, CaptureBundle bundle)
    {
        var provenance = AttributionModes.Provenance(bundle.Collector);
        var stockPerfView = provenance.Format == AttributionTraceFormat.PerfViewEtw;
        if ((!stockPerfView && provenance.Format != AttributionTraceFormat.WindowsEtw) || !provenance.SystemCpuReceptionConsented ||
            provenance.TargetOnlyArtifact == stockPerfView || provenance.CpuSampleIntervalNanoseconds is not > 0)
            throw new InvalidDataException("Windows ETW provenance, privacy scope, or sampling-interval evidence is missing.");
        var decoded = Inspect(path, indexPath, bundle.Target.Identity.ProcessId, targetOnlyArtifact: !stockPerfView);
        RequireCompleteCapture(bundle, decoded);
        var headers = new Dictionary<string, string>(decoded.Headers, StringComparer.Ordinal)
        {
            ["ExpectedCPUSamplingRate"] = provenance.CpuSampleIntervalNanoseconds.Value.ToString(CultureInfo.InvariantCulture),
            ["Scope"] = provenance.CpuScope,
            ["SamplingIntervalEvidence"] = stockPerfView ? "Pinned stock PerfView /CpuSampleMSec:1 configuration; independent count-versus-process-CPU calibration required." :
                "Legacy collector's independently queried native CPU interval.",
            ["EvidenceState"] = "Completed capture; report identity/window validation still required"
        };
        return decoded with { Headers = headers };
    }

    public static void RequireCompleteCapture(CaptureBundle bundle, DecodedTrace decoded)
    {
        var provenance = AttributionModes.Provenance(bundle.Collector);
        var controllerEvidenceMissing = provenance.Format == AttributionTraceFormat.WindowsEtw &&
            (provenance.NativeEventsLost is null || provenance.NativeBuffersLost is null);
        if (bundle.Collector.Phase != "complete" || !bundle.Target.Window.Complete || controllerEvidenceMissing ||
            provenance.NativeEventsLost is > 0 || provenance.NativeBuffersLost is > 0 || decoded.EventsLost != 0)
            throw new InvalidDataException($"Windows capture is incomplete or has missing/nonzero loss evidence: " +
                $"collector {bundle.Collector.Phase}, native events {provenance.NativeEventsLost}, " +
                $"native buffers {provenance.NativeBuffersLost}, ETL events {decoded.EventsLost}.");
        var markers = decoded.Markers.Where(marker =>
            marker.Payload.GetValueOrDefault("runId") == bundle.JobId.ToString("N")).ToArray();
        var starts = markers.Where(marker => marker.Payload.GetValueOrDefault("phase") == "measure-start").ToArray();
        var ends = markers.Where(marker => marker.Payload.GetValueOrDefault("phase") == "measure-end").ToArray();
        if (starts.Length != 1 || ends.Length != 1 ||
            !decoded.Cpu.Any(sample => sample.Milliseconds >= starts[0].Milliseconds && sample.Milliseconds < ends[0].Milliseconds))
            throw new InvalidDataException("Windows ETL has no target on-CPU observations in one unambiguous measurement window; no ready CPU report can be produced.");
    }

    // Offline inspection deliberately has no calibration/provenance and cannot admit a ready report.
    [SupportedOSPlatform("windows")]
    public static DecodedTrace Inspect(string path, string indexPath, int pid, bool targetOnlyArtifact = true)
    {
        if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
        if (File.Exists(indexPath)) throw new IOException("Offline ETL index must be a new derived file.");
        if (new FileInfo(path).Length > 512L * 1024 * 1024)
            throw new InvalidDataException("Windows ETL exceeds the 512 MiB input limit.");
        var rawEvents = 0;
        int rawLost;
        using (var raw = new ETWTraceEventSource(path, TraceEventSourceType.FileOnly))
        {
            raw.Kernel.All += _ => { };
            raw.Clr.All += _ => { };
            new ClrRundownTraceEventParser(raw).All += _ => { };
            raw.Dynamic.All += _ => { };
            raw.AllEvents += data =>
            {
                if (++rawEvents > (targetOnlyArtifact ? 2_000_000 : 10_000_000))
                    throw new InvalidDataException("ETL event limit exceeded.");
                if (data is EventTraceHeaderTraceData) return;
                if (targetOnlyArtifact && data.ProcessID != pid)
                    throw new InvalidDataException("Target-filtered Windows ETL contains a foreign or unresolved process identity.");
                if (targetOnlyArtifact && data is SampledProfileTraceData { NonProcess: true })
                    throw new InvalidDataException("Interrupt/DPC CPU cannot be attributed to the lab process.");
            };
            raw.Process();
            rawLost = raw.EventsLost;
        }
        TraceLog.CreateFromEventTraceLogFile(path, indexPath);
        using var trace = new TraceLog(indexPath);
        using var source = trace.Events.GetSource();
        var cpu = new List<CpuObservation>();
        var allocations = new List<AllocationObservation>();
        var markers = new List<MarkerObservation>();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var frames = 0;
        source.Dynamic.All += _ => { };
        source.Kernel.PerfInfoSample += sample =>
        {
            if (!targetOnlyArtifact && (sample.ProcessID != pid || sample.NonProcess)) return;
            if (sample.ProcessID != pid || sample.NonProcess || sample.Count <= 0)
                throw new InvalidDataException("Invalid target on-CPU sample identity/weight.");
            if (cpu.Count >= 1_000_000) throw new InvalidDataException("ETW sample bound exceeded.");
            cpu.Add(new(sample.TimeStampRelativeMSec, sample.ProcessID, sample.ThreadID, (ulong)sample.Count,
                NetTraceDecoder.Frames(sample, ref frames)));
        };
        source.Clr.AllocationSampling += sample =>
        {
            if (!targetOnlyArtifact && sample.ProcessID != pid) return;
            if (sample.ProcessID != pid) throw new InvalidDataException("Foreign allocation sample.");
            if (allocations.Count >= 1_000_000) throw new InvalidDataException("ETW allocation bound exceeded.");
            allocations.Add(new(sample.TimeStampRelativeMSec, sample.ProcessID, sample.ThreadID, sample.ObjectSize,
                sample.SampledByteOffset, sample.TypeName, NetTraceDecoder.Frames(sample, ref frames)));
        };
        source.AllEvents += data =>
        {
            if (data.ProcessID != pid && data is not EventTraceHeaderTraceData) return;
            var key = data.ProviderName + "/" + data.EventName;
            if (counts.Count >= 10_000 && !counts.ContainsKey(key)) throw new InvalidDataException("ETW metadata bound exceeded.");
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (data.ProviderName == "RuntimeMetricsLab-Attribution")
            {
                if (markers.Count >= 10_000) throw new InvalidDataException("ETW marker bound exceeded.");
                markers.Add(NetTraceDecoder.Describe(data));
            }
        };
        source.Process();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Format"] = "Windows ETL; true sampled kernel CPU, not EventPipe sampled-thread-time",
            ["EvidenceState"] = "Offline events only; no completed capture or sample-interval calibration inferred"
        };
        var identities = markers.Where(marker => marker.EventName.EndsWith("/Identity", StringComparison.Ordinal)).ToArray();
        return new(cpu.ToArray(), allocations.ToArray(), markers.ToArray(), identities, Math.Max(trace.EventsLost, rawLost), counts, headers);
    }
}
