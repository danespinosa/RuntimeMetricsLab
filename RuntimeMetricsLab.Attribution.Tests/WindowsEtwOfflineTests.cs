using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using RuntimeMetricsLab.Analysis;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Attribution.Tests;

public sealed class WindowsEtwOfflineTests
{
    [WindowsOfflineEvidenceTheory]
    [InlineData(0, 32, 34088)]
    [InlineData(1, 36, 34297)]
    public void PreservedRealFailedEtls_DecodeWithoutRepairingOrAdmittingThem(int index, int allocations, int losses)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native ETL decoding requires Windows.");
        var inputs = JsonSerializer.Deserialize<EvidenceInput[]>(File.ReadAllText(
            Environment.GetEnvironmentVariable(WindowsOfflineEvidenceTheoryAttribute.InputVariable)!))!;
        Assert.Equal(2, inputs.Length);
        var input = inputs[index];
        var originalCollector = File.ReadAllText(input.CollectorPath);
        var originalTarget = File.ReadAllText(input.TargetPath);
        Assert.Equal(input.Sha256, Hash(input.TracePath));
        var collector = JsonSerializer.Deserialize<CollectorStatus>(originalCollector)!;
        var target = JsonSerializer.Deserialize<TargetStatus>(originalTarget)!;
        var measurement = Assert.IsType<TargetMeasurement>(target.Measurement);
        var derived = Path.Combine(Path.GetTempPath(), $"rml-offline-etl-{Guid.NewGuid():N}.etlx");
        try
        {
            var decoded = WindowsEtwDecoder.Inspect(input.TracePath, derived, target.Identity.ProcessId);
            Assert.Empty(decoded.Cpu);
            Assert.Equal(allocations, decoded.Allocations.Length);
            Assert.All(decoded.Allocations, sample => Assert.Contains(sample.Frames, frame => !frame.Method.StartsWith('[')));
            Assert.Equal(losses, decoded.EventsLost);
            Assert.False(decoded.Headers.ContainsKey("ExpectedCPUSamplingRate"));
            var marker = Assert.Single(decoded.IdentityEvents);
            Assert.Equal(measurement.RunId.ToString("N"), marker.Payload["runId"]);
            Assert.Equal(target.Identity.InstanceId.ToString("N"), marker.Payload["instanceId"]);
            Assert.Equal(target.Identity.Environment["workload-module-mvid"], marker.Payload["moduleMvid"]);
            Assert.Equal(target.Identity.ProcessStartUtc, DateTimeOffset.Parse(marker.Payload["processStartUtc"], CultureInfo.InvariantCulture));
            Assert.Equal(measurement.Window.Start.MonotonicTicks.ToString(CultureInfo.InvariantCulture),
                Assert.Single(decoded.Markers, item => item.Payload.GetValueOrDefault("phase") == "measure-start").Payload["monotonicTicks"]);
            Assert.Equal(measurement.Window.End.MonotonicTicks.ToString(CultureInfo.InvariantCulture),
                Assert.Single(decoded.Markers, item => item.Payload.GetValueOrDefault("phase") == "measure-end").Payload["monotonicTicks"]);
            var failed = new CaptureBundle(1, measurement.RunId, measurement, collector, input.TracePath, DateTimeOffset.UtcNow);
            Assert.Equal("failed", collector.Phase);
            Assert.Throws<InvalidDataException>(() => WindowsEtwDecoder.RequireCompleteCapture(failed, decoded));
        }
        finally { if (File.Exists(derived)) File.Delete(derived); }
        Assert.Equal(input.Sha256, Hash(input.TracePath));
        Assert.Equal(originalCollector, File.ReadAllText(input.CollectorPath));
        Assert.Equal(originalTarget, File.ReadAllText(input.TargetPath));
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private sealed record EvidenceInput(string TracePath, string CollectorPath, string TargetPath, string Sha256);
}

public sealed class WindowsOfflineEvidenceTheoryAttribute : TheoryAttribute
{
    public const string InputVariable = "RUNTIMEMETRICSLAB_WINDOWS_ETL_EVIDENCE";
    public WindowsOfflineEvidenceTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(InputVariable)))
            Skip = "Opt-in native offline regression requires the two preserved user-capture paths; no profiler is launched.";
    }
}
