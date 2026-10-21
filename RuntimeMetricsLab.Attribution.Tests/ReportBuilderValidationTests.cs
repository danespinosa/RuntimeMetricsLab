using RuntimeMetricsLab.Analysis;

namespace RuntimeMetricsLab.Attribution.Tests;

public sealed class ReportBuilderValidationTests
{
    private static readonly Guid OtherRun = Guid.Parse("c088ab26-8313-4fbc-9361-3ca0372e7fe7");

    [Theory]
    [InlineData("schema")]
    [InlineData("target-run")]
    [InlineData("collector-operation")]
    public void Bundle_RequiresSupportedSchemaAndMatchingJobRunAndOperationIds(string fault)
    {
        var fixture = SyntheticCapture.Create();
        var bundle = fault switch
        {
            "schema" => fixture.Bundle with { SchemaVersion = 2 },
            "target-run" => fixture.Bundle with { Target = fixture.Bundle.Target with { RunId = OtherRun } },
            "collector-operation" => fixture.Bundle with { Collector = fixture.Bundle.Collector with { OperationId = OtherRun } },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        Assert.Throws<InvalidDataException>(() => ReportBuilder.Build(bundle, fixture.Trace));
    }

    [Theory]
    [InlineData(0L, 14000L)]
    [InlineData(-1000L, 14000L)]
    [InlineData(1000L, 10000L)]
    [InlineData(1000L, 9999L)]
    public void Window_RequiresPositiveFrequencyAndIncreasingMonotonicTicks(long frequency, long endTicks)
    {
        var fixture = SyntheticCapture.Create();
        var window = fixture.Bundle.Target.Window with
        {
            Frequency = frequency,
            End = fixture.Bundle.Target.Window.End with { MonotonicTicks = endTicks }
        };
        var bundle = fixture.Bundle with { Target = fixture.Bundle.Target with { Window = window } };

        Assert.Throws<InvalidDataException>(() => ReportBuilder.Build(bundle, fixture.Trace));
    }

    [Theory]
    [InlineData("measure-start", false)]
    [InlineData("measure-start", true)]
    [InlineData("measure-end", false)]
    [InlineData("measure-end", true)]
    public void NonceBoundaries_MustAppearExactlyOnce(string phase, bool duplicate)
    {
        var fixture = SyntheticCapture.Create();
        var marker = Assert.Single(fixture.Trace.Markers, item => item.Payload["phase"] == phase);
        MarkerObservation[] markers = duplicate ? [.. fixture.Trace.Markers, marker] :
            fixture.Trace.Markers.Where(item => item.Payload["phase"] != phase).ToArray();

        var error = Assert.Throws<InvalidDataException>(() =>
            ReportBuilder.Build(fixture.Bundle, fixture.Trace with { Markers = markers }));

        Assert.Contains("boundaries", error.Message);
    }

    [Theory]
    [InlineData("measure-start", "runId")]
    [InlineData("measure-end", "runId")]
    [InlineData("measure-start", "monotonicTicks")]
    [InlineData("measure-end", "monotonicTicks")]
    [InlineData("measure-start", "processId")]
    [InlineData("measure-end", "processId")]
    [InlineData("measure-start", "milliseconds")]
    [InlineData("measure-end", "milliseconds")]
    public void NonceBoundaries_MustMatchWindowTicksDurationAndFixedTargetProcess(string phase, string field)
    {
        var fixture = SyntheticCapture.Create();
        var markers = fixture.Trace.Markers.Select(marker =>
        {
            if (marker.Payload["phase"] != phase)
                return marker;
            return field switch
            {
                "runId" => marker with { Payload = new(marker.Payload) { ["runId"] = OtherRun.ToString("N") } },
                "monotonicTicks" => marker with { Payload = new(marker.Payload) { ["monotonicTicks"] = "999999" } },
                "processId" => marker with { ProcessId = 2 },
                "milliseconds" => marker with { Milliseconds = marker.Milliseconds + 100 },
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
        }).ToArray();

        Assert.Throws<InvalidDataException>(() => ReportBuilder.Build(fixture.Bundle, fixture.Trace with { Markers = markers }));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("event-name")]
    [InlineData("process-id")]
    [InlineData("namespace-id")]
    public void Identity_RequiresUniversalExistingProcessMappingForPidOneAndNamespaceOne(string fault)
    {
        var fixture = SyntheticCapture.Create();
        var identity = Assert.Single(fixture.Trace.IdentityEvents);
        MarkerObservation[] identities = fault switch
        {
            "missing" => [],
            "event-name" => [identity with { EventName = "Universal.System/OtherEvent" }],
            "process-id" => [identity with { ProcessId = 2 }],
            "namespace-id" => [identity with { Payload = new() { ["NamespaceId"] = "2" } }],
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        Assert.Throws<InvalidDataException>(() =>
            ReportBuilder.Build(fixture.Bundle, fixture.Trace with { IdentityEvents = identities }));
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("allocation")]
    public void Observations_FromAnotherProcessAreRejected(string kind)
    {
        var fixture = SyntheticCapture.Create();
        var trace = kind switch
        {
            "cpu" => fixture.Trace with { Cpu = [.. fixture.Trace.Cpu, fixture.Trace.Cpu[0] with { ProcessId = 2 }] },
            "allocation" => fixture.Trace with { Allocations = [.. fixture.Trace.Allocations, fixture.Trace.Allocations[0] with { ProcessId = 2 }] },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        Assert.Throws<InvalidDataException>(() => ReportBuilder.Build(fixture.Bundle, trace));
    }

    [Fact]
    public void MarkersForOtherRunNonces_DoNotCountAsDuplicateBoundaries()
    {
        var fixture = SyntheticCapture.Create();
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Markers =
            [
                .. fixture.Trace.Markers,
                SyntheticCapture.Marker(OtherRun, "measure-start", 999999, 999999),
                SyntheticCapture.Marker(OtherRun, "measure-end", 999999, 999999)
            ]
        });

        CostAssert.Equal(2, report.Process.CpuSeconds);
        CostAssert.Equal(5, SyntheticCapture.Method(report, "Arithmetic").CpuMillisecondsPerCall);
        Assert.DoesNotContain(report.Quality, notice => notice.Code == "incomplete");
    }
}
