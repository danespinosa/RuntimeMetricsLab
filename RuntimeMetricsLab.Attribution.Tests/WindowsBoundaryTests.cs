using System.Text.Json;
using RuntimeMetricsLab.Analysis;
using RuntimeMetricsLab.Collector.Windows;
using RuntimeMetricsLab.Contracts.Attribution;
using RuntimeMetricsLab.Experiments.Attribution;

namespace RuntimeMetricsLab.Attribution.Tests;

public sealed class WindowsBoundaryTests
{
    [Theory]
    [InlineData(null, AttributionMode.Standard)]
    [InlineData("standard", AttributionMode.Standard)]
    [InlineData("attribution", AttributionMode.Linux)]
    [InlineData("attribution-windows", AttributionMode.Windows)]
    public void Modes_PreserveDefaultAndLinuxOptIn(string? value, AttributionMode expected) =>
        Assert.Equal(expected, AttributionModes.Parse(value));

    [Fact]
    public void Modes_RejectUnrecognizedPlatforms() => Assert.Throws<ArgumentException>(() => AttributionModes.Parse("windows-container"));

    [Fact]
    public void ApprovalLaunchCollision_NeverDeletesAnExistingRecord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rml-etw-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "existing-owner");
            Assert.Throws<IOException>(() => WindowsApprovalFiles.CreateNew(path, _ => throw new Exception("Must not write")));
            Assert.Equal("existing-owner", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ApprovalLaunchFailedWrite_RemovesOnlyItsOwnPartialRecord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rml-etw-test-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Throws<IOException>(() => WindowsApprovalFiles.CreateNew(path, _ => throw new IOException("Injected write failure")));
            Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task PrivateControlProtocol_RoundTripsAndRejectsOversizedFrames()
    {
        await using var stream = new MemoryStream();
        var request = new WindowsCollectorRequest(new string('a', 64), WindowsCollectorOperation.Capabilities);
        await WindowsCollectorProtocol.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(request, await WindowsCollectorProtocol.ReadAsync<WindowsCollectorRequest>(stream, CancellationToken.None));
        await using var invalid = new MemoryStream([255, 255, 255, 127]);
        await Assert.ThrowsAsync<InvalidDataException>(() => WindowsCollectorProtocol.ReadAsync<WindowsCollectorRequest>(invalid, CancellationToken.None));
    }

    [Fact]
    public void Storage_DerivesGuidPathsAndAcceptsLegacyLinuxBundleReferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "RuntimeMetricsLab-tests");
        var paths = new AttributionPaths(Path.Combine(root, "data"), Path.Combine(root, "captures"));
        var bundle = SyntheticCapture.Create().Bundle;
        bundle = bundle with { TraceFile = $"/captures/{bundle.Collector.CaptureId:N}/trace.nettrace" };
        Assert.Equal(Path.Combine(root, "captures", bundle.Collector.CaptureId.ToString("N"), "trace.nettrace"), paths.ResolveTrace(bundle));
        Assert.Throws<InvalidDataException>(() => paths.ResolveTrace(bundle with { TraceFile = @"..\secret.etl" }));
        Assert.Throws<ArgumentException>(() => new AttributionPaths("relative-data", root));
        Assert.Throws<ArgumentException>(() => paths.Job(Guid.Empty));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("6c1746d62z")]
    [InlineData("")]
    public void LabIdentity_RejectsUnapprovedPathComponents(string labId) =>
        Assert.Throws<ArgumentException>(() => AttributionPaths.ValidateLabId(labId));

    [Fact]
    public void WindowsProvenance_RequiresConsentTargetIdentityAndNonceMarker()
    {
        var fixture = WindowsFixture();
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace);
        CostAssert.Equal(0.5, report.Process.AverageCores);
        Assert.Contains("Windows-ETW", report.AdapterVersion);
        Assert.Contains(report.Quality, item => item.Code == "windows-collection-scope");
        var bad = fixture.Bundle with { Collector = fixture.Bundle.Collector with
        {
            Provenance = fixture.Bundle.Collector.Provenance! with { SystemCpuReceptionConsented = false }
        } };
        Assert.Throws<InvalidDataException>(() => ReportBuilder.Build(bad, fixture.Trace));
        Assert.Throws<InvalidDataException>(() => ReportBuilder.Build(fixture.Bundle, fixture.Trace with { IdentityEvents = [] }));
        var stock = fixture.Bundle with { Collector = fixture.Bundle.Collector with
        {
            Provenance = fixture.Bundle.Collector.Provenance! with
            {
                Format = AttributionTraceFormat.PerfViewEtw, Collector = "Microsoft PerfView", Version = "3.2.8",
                TargetOnlyArtifact = false
            }
        } };
        Assert.Contains(ReportBuilder.Build(stock, fixture.Trace).Quality,
            notice => notice.Code == "windows-collection-scope" && notice.Explanation.Contains("system-wide"));
        Assert.Throws<InvalidDataException>(() => ReportBuilder.Build(stock with
        {
            Collector = stock.Collector with { Provenance = stock.Collector.Provenance! with { TargetOnlyArtifact = true } }
        }, fixture.Trace));
    }

    [Fact]
    public void WindowsNativeLoss_WithholdsAbsoluteMethodCpu()
    {
        var fixture = WindowsFixture();
        var bundle = fixture.Bundle with { Collector = fixture.Bundle.Collector with
        {
            Provenance = fixture.Bundle.Collector.Provenance! with { NativeBuffersLost = 1 }
        } };
        var report = ReportBuilder.Build(bundle, fixture.Trace);
        Assert.Null(report.Process.CoveredCpuSeconds);
        Assert.All(report.Methods, method => Assert.Null(method.CpuMillisecondsPerCall));
        Assert.Contains(report.Quality, item => item.Code == "native-etw-loss");
    }

    [Fact]
    public void WindowsStartIdentity_AcceptsEquivalentOffsetButRejectsADifferentInstant()
    {
        var fixture = WindowsFixture();
        var marker = fixture.Trace.IdentityEvents.Single();
        var payload = new Dictionary<string, string>(marker.Payload)
        {
            ["processStartUtc"] = fixture.Bundle.Target.Identity.ProcessStartUtc!.Value.ToOffset(TimeSpan.FromHours(-7)).ToString("O")
        };
        var trace = fixture.Trace with { IdentityEvents = [marker with { Payload = payload }] };
        Assert.NotEmpty(ReportBuilder.Build(fixture.Bundle, trace).Methods);
        payload["processStartUtc"] = fixture.Bundle.Target.Identity.ProcessStartUtc.Value.AddTicks(1).ToString("O");
        Assert.Throws<InvalidDataException>(() => ReportBuilder.Build(fixture.Bundle, trace));
    }

    [Fact]
    public void WindowsEmptyCpuOrLostEvents_CannotEnterTheReadyReportPath()
    {
        var fixture = WindowsFixture();
        Assert.Throws<InvalidDataException>(() => WindowsEtwDecoder.RequireCompleteCapture(fixture.Bundle, fixture.Trace with { Cpu = [] }));
        Assert.Throws<InvalidDataException>(() => WindowsEtwDecoder.RequireCompleteCapture(fixture.Bundle, fixture.Trace with { EventsLost = 34088 }));
        Assert.Throws<InvalidDataException>(() => WindowsEtwDecoder.RequireCompleteCapture(
            fixture.Bundle with { Collector = fixture.Bundle.Collector with { Phase = "failed" } }, fixture.Trace));
    }

    [Theory]
    [InlineData(AttributionTraceFormat.UniversalNetTraceV6, false, 4)]
    [InlineData(AttributionTraceFormat.UniversalNetTraceV6, true, 4)]
    [InlineData(AttributionTraceFormat.WindowsEtw, false, 4)]
    [InlineData(AttributionTraceFormat.WindowsEtw, true, 5)]
    public void ArtifactNames_KeepLegacyBundlesAndIncludeOptionalWindowsRundown(
        AttributionTraceFormat format, bool includeRundown, int expectedCount)
    {
        var files = AttributionArtifacts.CollectorFiles(format, includeRundown);
        Assert.Equal(expectedCount, files.Length);
        Assert.Contains(AttributionModes.TraceFileName(format), files);
        Assert.Equal(format == AttributionTraceFormat.WindowsEtw && includeRundown,
            files.Contains(AttributionArtifacts.WindowsRundown));
        Assert.All(files, name => Assert.Equal(Path.GetFileName(name), name));
    }

    [Fact]
    public void CounterOnlyEvidence_HasNoInventedMethodCpuFields()
    {
        var fixture = WindowsFixture();
        var report = new CounterDiagnosticReport(1, fixture.Bundle.JobId, fixture.Bundle.Target, 4, 2, 0.5, 512);
        var json = JsonSerializer.Serialize(report);
        Assert.Contains("No on-CPU profile", report.Limitation);
        Assert.DoesNotContain("SelfAverageCores", json);
        Assert.DoesNotContain("CpuMillisecondsPerCall", json);
    }

    [Fact]
    public void NativeWindowsIdentity_RecordsGroupsAndNeverReadsCgroups()
    {
        if (!OperatingSystem.IsWindows()) return;
        var identity = new WindowsTargetEnvironment().ReadIdentity();
        Assert.Equal(AttributionPlatform.WindowsProcess, identity.Platform);
        Assert.Null(identity.PidNamespace);
        Assert.NotNull(identity.ProcessStartUtc);
        Assert.NotNull(identity.UserSid);
        Assert.Equal(Environment.ProcessorCount, identity.ProcessorCount);
        Assert.Contains("active-processor-groups", identity.Environment.Keys);
        Assert.DoesNotContain("cpu.max", identity.Environment.Keys);
    }

    private static SyntheticCapture WindowsFixture()
    {
        var fixture = SyntheticCapture.Create();
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
        var identity = fixture.Bundle.Target.Identity with
        {
            Platform = AttributionPlatform.WindowsProcess, PidNamespace = null, ProcessStartUtc = start,
            Environment = new() { ["workload-module-mvid"] = "known-module" }
        };
        var provenance = new CaptureProvenance(AttributionPlatform.WindowsProcess, AttributionTraceFormat.WindowsEtw,
            "Windows ETW fixture", "3.2.8", "Consented kernel stream with immediate target-only relogging", "CLR PID filter",
            1_000_000, true, true, 0, 0, 100);
        var collector = fixture.Bundle.Collector with { Provenance = provenance, TargetProcessId = 1, TargetProcessStartUtc = start, KernelLostEvents = 0 };
        var marker = new MarkerObservation(0, 1, "RuntimeMetricsLab-Attribution/Identity", new()
        {
            ["runId"] = fixture.Bundle.JobId.ToString("N"), ["instanceId"] = identity.InstanceId.ToString("N"),
            ["processStartUtc"] = start.ToString("O"), ["moduleMvid"] = "known-module"
        });
        return fixture with { Bundle = fixture.Bundle with { Target = fixture.Bundle.Target with { Identity = identity }, Collector = collector },
            Trace = fixture.Trace with { IdentityEvents = [marker] } };
    }
}
