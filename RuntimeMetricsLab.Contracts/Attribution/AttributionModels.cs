namespace RuntimeMetricsLab.Contracts.Attribution;

public sealed record LessonSettings(string Lesson = "cpu", int Workers = 1, int DurationSeconds = 5,
    int AllocationMiB = 64, int RetainedMiB = 16);
public sealed record StartAttributionRequest(Guid IdempotencyKey, LessonSettings Settings,
    AttributionRunKind Kind = AttributionRunKind.Profile);
public sealed record PrepareTargetRequest(Guid RunId, LessonSettings Settings);
public sealed record CollectorStartRequest(Guid OperationId, string Target, int DurationSeconds);
public sealed record CollectorStopRequest(bool Cancelled);
public sealed record TargetIdentity(Guid InstanceId, int ProcessId, long StartTicks, string? PidNamespace,
    string Runtime, int ProcessorCount, Dictionary<string, string?> Environment,
    AttributionPlatform Platform = AttributionPlatform.LinuxContainer, DateTimeOffset? ProcessStartUtc = null,
    string? UserSid = null);
public sealed record RawRuntimeValue(string Name, double Value, string Unit, Dictionary<string, string> Tags);
public sealed record RuntimePoint(long MonotonicTicks, DateTimeOffset Utc, string Phase, RawRuntimeValue[] Values);
public sealed record MethodCalls(string Method, long Entered, long Completed, long SameThreadAllocatedBytes);
public sealed record MeasurementWindow(RuntimePoint Start, RuntimePoint End, long Frequency, bool Complete);
public sealed record TargetMeasurement(Guid RunId, TargetIdentity Identity, LessonSettings Settings,
    MeasurementWindow Window, MethodCalls[] Calls, RuntimePoint[] Samples, long RetainedPayloadBytes,
    double IndependentCpuSeconds, long IndependentAllocatedBytes, long Checksum, string? Error);
public sealed record TargetStatus(Guid? RunId, string Phase, bool ProfilerEnabled, TargetIdentity Identity,
    TargetMeasurement? Measurement, string? Error);
public sealed record CollectorStatus(Guid CaptureId, Guid OperationId, string Phase, int? ExitCode,
    string? Sha256, long Bytes, string? Error, string ToolVersion = "10.0.750501",
    string OneCollectVersion = "0.1.35021", string? TargetStartTime = null, string? PidNamespace = null,
    int DurationSeconds = 0, long? KernelLostEvents = null, CaptureProvenance? Provenance = null,
    int? TargetProcessId = null, DateTimeOffset? TargetProcessStartUtc = null);
public sealed record CapabilityStatus(bool Available, string[] Evidence, string? Error,
    AttributionPlatform? Platform = null, bool RequiresElevation = false, bool RequiresSystemCpuConsent = false,
    string? Remediation = null);
public sealed record AttributionJob(Guid Id, Guid IdempotencyKey, LessonSettings Settings, DateTimeOffset Created,
    DateTimeOffset Updated, string Phase, Guid? CaptureId = null, string? Error = null, int ReportRevision = 0,
    string? DetailPhase = null, AttributionRunKind Kind = AttributionRunKind.Profile,
    WindowsProfilerApproval? ProfilerApproval = null)
{
    public bool IsTerminal => Phase is "ready" or "diagnostic-ready" or "partial" or "cancelled" or "failed";
}
public sealed record CaptureBundle(int SchemaVersion, Guid JobId, TargetMeasurement Target,
    CollectorStatus Collector, string TraceFile, DateTimeOffset FinalizedUtc, Dictionary<string, string>? ArtifactHashes = null);
public sealed record QualityNotice(string Code, string Severity, string Explanation);
public sealed record ProcessCosts(double Seconds, double? CpuSeconds, double? AverageCores,
    double? PercentOfOneCore, double? PercentOfRuntimeCapacity, double? AllocatedBytes,
    double? AllocationBytesPerSecond, double? NominalSampledCpuSeconds, double? CpuSamplingRatio,
    double? AllocationSamplingRatio, double? UncoveredCores, double? CoveredCpuSeconds = null);
public sealed record MethodCosts(string Key, string Module, string Method, string Category,
    double CpuSamples, long AllocationSamples, double? SelfCpuMilliseconds, double? InclusiveCpuMilliseconds,
    double? SelfAverageCores, double? InclusiveAverageCores, double? ProcessCpuShare,
    double AllocatedBytes, double InclusiveAllocatedBytes, double AllocationBytesPerSecond,
    long? CompletedCalls, double? CpuMillisecondsPerCall, double? AllocatedBytesPerCall,
    long? SameThreadAllocatedBytes);
public sealed record CallNode(string Id, string MethodKey, string Name, double CpuWeight, double SelfCpuWeight,
    double AllocationBytes, double SelfAllocationBytes, CallNode[] Children);
public sealed record AttributionReport(int SchemaVersion, Guid JobId, string AdapterVersion,
    MeasurementWindow Window, TargetIdentity Identity, ProcessCosts Process, MethodCosts[] Methods,
    CallNode Tree, RuntimePoint[] Samples, QualityNotice[] Quality, string[] Explanations,
    int EventsLost, Dictionary<string, string> TraceHeaders, CollectorStatus Collector, int Revision = 1);

public static class AttributionMetrics
{
    public static double? Value(RuntimePoint point, string name, string? tagName = null, string? tagValue = null)
    {
        var values = point.Values.Where(value => value.Name == name &&
            (tagName is null || value.Tags.GetValueOrDefault(tagName) == tagValue)).ToArray();
        return values.Length == 0 ? null : values.Sum(value => value.Value);
    }

    public static double? Delta(MeasurementWindow window, string name, string? tagName = null, string? tagValue = null)
    {
        var start = Value(window.Start, name, tagName, tagValue);
        var end = Value(window.End, name, tagName, tagValue);
        return start.HasValue && end.HasValue && end >= start ? end - start : null;
    }

    public static double? IntervalCores(MeasurementWindow window)
    {
        var seconds = IntervalSeconds(window);
        var cpu = Delta(window, "dotnet.process.cpu.time", "cpu.mode", "user") +
            Delta(window, "dotnet.process.cpu.time", "cpu.mode", "system");
        return seconds > 0 ? cpu / seconds : null;
    }

    public static double? IntervalAllocationRate(MeasurementWindow window)
    {
        var seconds = IntervalSeconds(window);
        return seconds > 0 ? Delta(window, "dotnet.gc.heap.total_allocated") / seconds : null;
    }

    private static double IntervalSeconds(MeasurementWindow window) => window.Frequency > 0
        ? ((double)window.End.MonotonicTicks - window.Start.MonotonicTicks) / window.Frequency : 0;
}

public static class AttributionBudgets
{
    public const int AttachmentSeconds = 20;
    public const int MaximumWorkSeconds = 60;
    public const int MaximumCaptureSeconds = MaximumWorkSeconds + 30;
    public static int CaptureSeconds(int workSeconds) => checked(workSeconds + 30);
    public static int CaptureSeconds(int workSeconds, AttributionMode mode) =>
        mode == AttributionMode.Windows ? checked(workSeconds + 8) : CaptureSeconds(workSeconds);
    public static int FinalizationSeconds(AttributionMode mode) => mode == AttributionMode.Windows ? 300 : 25;
    public static int JobSeconds(int workSeconds, AttributionMode mode) => checked(workSeconds +
        (mode == AttributionMode.Windows ? 360 : 120));
}

public static class AttributionJson
{
    // Up to 256 stack frames, each represented by an object plus a Children array.
    public static System.Text.Json.JsonSerializerOptions ReportOptions { get; } = new() { MaxDepth = 600 };
}
