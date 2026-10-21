namespace RuntimeMetricsLab.Contracts.Attribution;

public sealed class AttributionPaths
{
    public string DataRoot { get; }
    public string CapturesRoot { get; }
    public string JobsRoot => Path.Combine(DataRoot, "jobs");
    public string ExportsRoot => Path.Combine(DataRoot, "exports");
    public string TemporaryRoot => Path.Combine(DataRoot, "temp");

    public AttributionPaths(string dataRoot, string capturesRoot)
    {
        if (!Path.IsPathFullyQualified(dataRoot) || !Path.IsPathFullyQualified(capturesRoot))
            throw new ArgumentException("Attribution storage roots must be absolute local paths.");
        DataRoot = Path.GetFullPath(dataRoot);
        CapturesRoot = Path.GetFullPath(capturesRoot);
        if (OperatingSystem.IsWindows() && (DataRoot.StartsWith(@"\\") || CapturesRoot.StartsWith(@"\\")))
            throw new ArgumentException("Attribution storage must remain local, not a network share.");
    }

    public string Job(Guid id) => Path.Combine(JobsRoot, ValidId(id));
    public string Capture(Guid id) => Path.Combine(CapturesRoot, ValidId(id));
    public string Trace(CollectorStatus capture) => Path.Combine(Capture(capture.CaptureId),
        AttributionModes.TraceFileName(AttributionModes.Provenance(capture).Format));

    public string ResolveTrace(CaptureBundle bundle)
    {
        var name = AttributionModes.TraceFileName(AttributionModes.Provenance(bundle.Collector).Format);
        var legacy = $"/captures/{bundle.Collector.CaptureId:N}/{name}";
        var expected = Trace(bundle.Collector);
        if (bundle.TraceFile != name && bundle.TraceFile != legacy &&
            !string.Equals(bundle.TraceFile, expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Bundle trace reference is outside its approved capture location.");
        return expected;
    }

    public static AttributionPaths Linux()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux defaults require Linux.");
        return new("/data", "/captures");
    }

    public static string WindowsDataRoot(string labId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RuntimeMetricsLab", "Attribution", ValidateLabId(labId));

    public static string WindowsCaptureRoot(string labId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RuntimeMetricsLab", "Attribution", ValidateLabId(labId), "captures");

    public static string ValidateLabId(string labId)
    {
        if (labId.Length != 10 || labId.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("Expected a ten-character hexadecimal lab identity.");
        return labId.ToLowerInvariant();
    }

    private static string ValidId(Guid id) => id != Guid.Empty ? id.ToString("N") : throw new ArgumentException("Empty artifact identity.");
}
