using System.Globalization;
using RuntimeMetricsLab.Analysis;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Attribution.Tests;

// In-memory decoded observations only: this fixture does not exercise collectors, trace decoding, or Linux integration.
internal sealed record SyntheticCapture(CaptureBundle Bundle, DecodedTrace Trace)
{
    public const long MiB = 1024 * 1024;
    public const long Frequency = 1000;
    public const long StartTicks = 10000;
    public const double TraceStartMilliseconds = 1000;
    public const long SampleObjectSize = 8 * MiB;
    public const string LabModule = "runtimemetricslab.experiments";
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public AttributionReport Build() => ReportBuilder.Build(Bundle, Trace);

    public static SyntheticCapture Create(double seconds = 4, double cpuSeconds = 2, int processorCount = 4)
    {
        var runId = Guid.Parse("02cb50b4-52dc-4aae-ad0a-ddcfe1c9a9d6");
        var identity = new TargetIdentity(Guid.Parse("d4454e0c-5735-4af3-aebe-82881b126f68"),
            1, 100, "pid:[4026533001]", ".NET 10.0.0", processorCount, new());
        var settings = new LessonSettings("mixed", 4, (int)Math.Ceiling(seconds), 512, 16);
        var start = CounterPoint(StartTicks, "measure-start", 10, 4, 100 * MiB);
        var end = CounterPoint(StartTicks + (long)(seconds * Frequency), "measure-end",
            10 + cpuSeconds * 0.75, 4 + cpuSeconds * 0.25, 612 * MiB);
        var window = new MeasurementWindow(start, end, Frequency, true);
        var target = new TargetMeasurement(runId, identity, settings, window,
            [
                new("Arithmetic", 300, 300, 0),
                new("Checksum", 100, 100, 0),
                new("AllocateLarge", 4096, 4096, 512 * MiB),
                new("AllocateSmall", 4096, 4096, 0),
                new("MixedPipeline", 400, 400, 512 * MiB)
            ],
            [start, end], 16 * MiB, cpuSeconds, 512 * MiB, 123, null);
        var collector = new CollectorStatus(Guid.Parse("18cf9842-e58d-4eb3-88ba-46cb99cdd564"),
            runId, "complete", 0, new string('0', 64), 1, null,
            TargetStartTime: Epoch.ToString("O", CultureInfo.InvariantCulture), PidNamespace: identity.PidNamespace);
        var bundle = new CaptureBundle(1, runId, target, collector, "synthetic-only.nettrace", end.Utc);
        var totalSamples = (int)Math.Round(cpuSeconds * 1000);
        var arithmeticSamples = totalSamples * 3 / 4;
        var cpu = CpuSamples(arithmeticSamples, [LabFrame("Arithmetic"), LabFrame("MixedPipeline")], seconds)
            .Concat(CpuSamples(totalSamples - arithmeticSamples, [LabFrame("Checksum"), LabFrame("MixedPipeline")], seconds))
            .ToArray();
        var allocations = AllocationSamples(64,
            [
                new("vmlinux", "user_events_write"),
                new("libcoreclr.so", "EmitAllocationSample"),
                LabFrame("AllocateLarge"),
                LabFrame("MixedPipeline")
            ], seconds);
        var markers = new[]
        {
            Marker(runId, "attached", TraceStartMilliseconds - 1, StartTicks - 1),
            Marker(runId, "measure-start", TraceStartMilliseconds, start.MonotonicTicks),
            Marker(runId, "measure-end", TraceStartMilliseconds + seconds * 1000, end.MonotonicTicks)
        };
        var trace = new DecodedTrace(cpu, allocations, markers,
            [new(0, 1, "Universal.System/ExistingProcess", new() { ["NamespaceId"] = "1" })],
            0, new(StringComparer.Ordinal),
            new(StringComparer.Ordinal) { ["ExpectedCPUSamplingRate"] = "1000000" });
        return new(bundle, trace);
    }

    public static ProfileFrame LabFrame(string method) =>
        new(LabModule, $"RuntimeMetricsLab.Experiments.Attribution.AttributionLessons::{method}()");

    public static CpuObservation[] CpuSamples(int count, ProfileFrame[] frames, double seconds = 4) =>
        Enumerable.Range(0, count)
            .Select(index => new CpuObservation(TraceStartMilliseconds + (index + 0.5) * seconds * 1000 / count,
                1, 10, 1, frames)).ToArray();

    public static AllocationObservation[] AllocationSamples(int count, ProfileFrame[] frames, double seconds = 4) =>
        Enumerable.Range(0, count)
            .Select(index => new AllocationObservation(TraceStartMilliseconds + (index + 0.5) * seconds * 1000 / count,
                1, 10, SampleObjectSize, 0, "System.Byte[]", frames)).ToArray();

    public static RuntimePoint CounterPoint(long ticks, string phase, double userCpu, double systemCpu, double? allocated)
    {
        var values = new List<RawRuntimeValue>
        {
            new("dotnet.process.cpu.time", userCpu, "s", new() { ["cpu.mode"] = "user" }),
            new("dotnet.process.cpu.time", systemCpu, "s", new() { ["cpu.mode"] = "system" })
        };
        if (allocated.HasValue)
            values.Add(new("dotnet.gc.heap.total_allocated", allocated.Value, "By", new()));
        return new(ticks, Epoch.AddSeconds(ticks / (double)Frequency), phase, values.ToArray());
    }

    public static MarkerObservation Marker(Guid runId, string phase, double milliseconds, long ticks) =>
        new(milliseconds, 1, "RuntimeMetricsLab-Attribution/Boundary",
            new(StringComparer.Ordinal)
            {
                ["runId"] = runId.ToString("N"),
                ["phase"] = phase,
                ["monotonicTicks"] = ticks.ToString(CultureInfo.InvariantCulture)
            });

    public static MethodCosts Method(AttributionReport report, string method) =>
        Assert.Single(report.Methods, item => item.Key == LabModule + "!" + LabFrame(method).Method);
}

internal static class CostAssert
{
    public static void Equal(double expected, double? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value, 9);
    }
}
