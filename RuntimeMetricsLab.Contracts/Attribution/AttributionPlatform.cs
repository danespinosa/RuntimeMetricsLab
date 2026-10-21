namespace RuntimeMetricsLab.Contracts.Attribution;

public enum AttributionPlatform { LinuxContainer, WindowsProcess }
public enum AttributionRunKind { Profile, CounterDiagnostic }
public enum AttributionTraceFormat { UniversalNetTraceV6, WindowsEtw, PerfViewEtw }
public enum AttributionMode { Standard, Linux, Windows }

public sealed record CaptureProvenance(AttributionPlatform Platform, AttributionTraceFormat Format,
    string Collector, string Version, string CpuScope, string AllocationScope,
    double? CpuSampleIntervalNanoseconds, bool SystemCpuReceptionConsented, bool TargetOnlyArtifact,
    long? NativeEventsLost = null, long? NativeBuffersLost = null, long? RejectedEvents = null)
{
    public static CaptureProvenance Linux { get; } = new(AttributionPlatform.LinuxContainer,
        AttributionTraceFormat.UniversalNetTraceV6, "dotnet-trace collect-linux / OneCollect", "10.0.750501 / 0.1.35021",
        "Target-only shared PID namespace; perf on-CPU samples", "Target runtime randomized AllocationSampled",
        null, false, true);
}

public sealed record CounterDiagnosticReport(int SchemaVersion, Guid JobId, TargetMeasurement Measurement,
    double Seconds, double? ProcessCpuSeconds, double? ProcessAverageCores, double? AllocatedBytes,
    string Limitation = "Counter-only diagnostic. No on-CPU profile or method CPU attribution. Same-thread allocation is measured inclusive calibration, not sampled allocation ownership.");

public static class AttributionModes
{
    public static AttributionMode Parse(string? mode) => mode switch
    {
        null or "" or "standard" => AttributionMode.Standard,
        "attribution" => AttributionMode.Linux,
        "attribution-windows" => AttributionMode.Windows,
        _ => throw new ArgumentException("RuntimeMetricsLab:Mode must be standard, attribution, or attribution-windows.")
    };

    public static string TargetAlias(AttributionMode mode) => mode switch
    {
        AttributionMode.Linux => "linux-workload",
        AttributionMode.Windows => "windows-workload",
        _ => throw new InvalidOperationException("Standard mode has no attribution target.")
    };

    public static string TraceFileName(AttributionTraceFormat format) => format switch
    {
        AttributionTraceFormat.UniversalNetTraceV6 => "trace.nettrace",
        AttributionTraceFormat.WindowsEtw or AttributionTraceFormat.PerfViewEtw => "trace.etl",
        _ => throw new InvalidDataException("Unsupported capture format.")
    };

    public static CaptureProvenance Provenance(CollectorStatus status) => status.Provenance ?? CaptureProvenance.Linux;
}

public static class AttributionArtifacts
{
    public const string WindowsRundown = "rundown.json";

    public static string[] CollectorFiles(AttributionTraceFormat format, bool includeRundown)
    {
        string[] required = [AttributionModes.TraceFileName(format), "collector.json", "stdout.log", "stderr.log"];
        if (format == AttributionTraceFormat.PerfViewEtw)
            return [.. required, "perfview.json", "perfview.log", "session-list.log"];
        return format == AttributionTraceFormat.WindowsEtw && includeRundown
            ? [.. required, WindowsRundown] : required;
    }
}
