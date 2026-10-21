using System.Text.Json;
using RuntimeMetricsLab.Analysis;
using RuntimeMetricsLab.Contracts.Attribution;
using System.Security.Cryptography;

if (args.Length is 2 or 6 && args[0] == "--job" && Guid.TryParseExact(args[1], "N", out var jobId))
{
    var paths = args.Length == 6 && args[2] == "--data-root" && args[4] == "--capture-root"
        ? new AttributionPaths(args[3], args[5])
        : AttributionPaths.Linux();
    if (OperatingSystem.IsWindows())
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Analysis must run non-elevated.");
    }
    var directory = paths.Job(jobId);
    var bundle = JsonSerializer.Deserialize<CaptureBundle>(await File.ReadAllTextAsync(Path.Combine(directory, "bundle.json"))) ??
        throw new InvalidDataException("Capture bundle is missing.");
    if (bundle.JobId != jobId)
        throw new InvalidDataException("Worker job identity does not match the bundle.");
    var expectedTracePath = paths.ResolveTrace(bundle);
    var provenance = AttributionModes.Provenance(bundle.Collector);
    var traceName = AttributionModes.TraceFileName(provenance.Format);
    if (bundle.ArtifactHashes is { } hashes)
    {
        foreach (var name in AttributionArtifacts.CollectorFiles(provenance.Format,
            hashes.ContainsKey(AttributionArtifacts.WindowsRundown)).Prepend("target.json"))
        {
            var path = name == "target.json" ? Path.Combine(directory, name) : Path.Combine(Path.GetDirectoryName(expectedTracePath)!, name);
            await using var artifact = File.OpenRead(path);
            if (!hashes.TryGetValue(name, out var expected) || Convert.ToHexString(await SHA256.HashDataAsync(artifact)) != expected)
                throw new InvalidDataException($"Immutable artifact checksum does not match: {name}.");
        }
        bundle = bundle with { Target = JsonSerializer.Deserialize<TargetMeasurement>(await File.ReadAllTextAsync(Path.Combine(directory, "target.json"))) ??
            throw new InvalidDataException("Immutable target measurements are missing.") };
    }
    await using (var input = File.OpenRead(expectedTracePath))
    {
        var checksum = Convert.ToHexString(await SHA256.HashDataAsync(input));
        if (checksum != bundle.Collector.Sha256)
            throw new InvalidDataException("Immutable raw capture checksum does not match its manifest.");
    }
    var revision = Directory.EnumerateFiles(directory, "report-v*.json")
        .Select(path => int.Parse(Path.GetFileNameWithoutExtension(path)["report-v".Length..])).DefaultIfEmpty(0).Max() + 1;
    var reportPath = Path.Combine(directory, $"report-v{revision}.json");
    var index = Path.Combine(directory, $"trace-v{revision}.etlx");
    var decodedTrace = provenance.Format switch
    {
        AttributionTraceFormat.UniversalNetTraceV6 => NetTraceDecoder.Decode(expectedTracePath, index),
        AttributionTraceFormat.WindowsEtw or AttributionTraceFormat.PerfViewEtw when OperatingSystem.IsWindows() =>
            WindowsEtwDecoder.Decode(expectedTracePath, index, bundle),
        _ => throw new PlatformNotSupportedException("This trace format requires its supported platform adapter; no fallback is used.")
    };
    var report = ReportBuilder.Build(bundle, decodedTrace)
        with { Revision = revision };
    var temporary = reportPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        await using (var output = new FileStream(temporary, FileMode.CreateNew))
            await JsonSerializer.SerializeAsync(output, report, AttributionJson.ReportOptions);
        File.Move(temporary, reportPath);
    }
    finally
    {
        if (File.Exists(temporary)) File.Delete(temporary);
    }
    Console.WriteLine($"Saved local report for {jobId:N}; {report.Methods.Length} methods.");
    return 0;
}

if (!OperatingSystem.IsLinux() || args.Length != 2 || !Path.GetFullPath(args[0]).StartsWith("/captures/", StringComparison.Ordinal) ||
    !Path.GetFullPath(args[1]).StartsWith("/tmp/", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: RuntimeMetricsLab.Analysis.Worker --job <N-format local job ID>. Compatibility inspection is restricted to /captures and /tmp.");
    return 2;
}

var decoded = NetTraceDecoder.Decode(args[0], args[1]);
var start = decoded.Markers.Single(marker => marker.Payload.GetValueOrDefault("phase") == "measure-start").Milliseconds;
var end = decoded.Markers.Single(marker => marker.Payload.GetValueOrDefault("phase") == "measure-end").Milliseconds;
var windowCpu = decoded.Cpu.Where(sample => sample.Milliseconds >= start && sample.Milliseconds < end).ToArray();
var windowAllocation = decoded.Allocations.Where(sample => sample.Milliseconds >= start && sample.Milliseconds < end).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new
{
    Parser = typeof(Microsoft.Diagnostics.Tracing.Etlx.TraceLog).Assembly.GetName().Version?.ToString(),
    decoded.EventsLost,
    decoded.Headers,
    Window = new
    {
        Start = start,
        End = end,
        CpuWeight = windowCpu.Sum(sample => (double)sample.Weight),
        AllocationSamples = windowAllocation.Length,
        EstimatedBytes = windowAllocation.Sum(sample => AllocationEstimator.EstimatedBytes(sample.ObjectSize)),
        NamedAllocationSamples = windowAllocation.Count(sample => sample.Frames.Any(frame => frame.Method.Contains("NamedAllocate"))),
        NamedAllocationBytes = windowAllocation.Where(sample => sample.Frames.Any(frame => frame.Method.Contains("NamedAllocate")))
            .Sum(sample => AllocationEstimator.EstimatedBytes(sample.ObjectSize))
    },
    CpuSamples = decoded.Cpu.Length,
    CpuWeight = decoded.Cpu.Sum(sample => (double)sample.Weight),
    CpuWeightValues = decoded.Cpu.Select(sample => sample.Weight).Distinct().ToArray(),
    CpuProcessIds = decoded.Cpu.GroupBy(sample => sample.ProcessId).Select(group => new { ProcessId = group.Key, Count = group.Count() }),
    Allocations = decoded.Allocations.Length,
    decoded.Markers,
    IdentityEvents = decoded.IdentityEvents.Where(data => !data.EventName.Contains("Mapping") && !data.EventName.Contains("Symbol")),
    decoded.EventCounts,
    TopCpu = decoded.Cpu.GroupBy(sample => sample.Frames[0]).OrderByDescending(group => group.Count()).Take(15)
        .Select(group => new { Frame = group.Key, Count = group.Count() }),
    TopAllocation = decoded.Allocations.GroupBy(sample => sample.Frames[0]).OrderByDescending(group => group.Count()).Take(10)
        .Select(group => new { Frame = group.Key, Count = group.Count() }),
    ExampleCpuStack = decoded.Cpu.FirstOrDefault(sample => sample.Frames.Any(frame => frame.Method.Contains("NamedArithmetic")))?.Frames,
    ExampleAllocationStack = windowAllocation.FirstOrDefault(sample => sample.Frames.Any(frame => frame.Method.Contains("NamedAllocate")))?.Frames
}, new JsonSerializerOptions { WriteIndented = true }));
return 0;
