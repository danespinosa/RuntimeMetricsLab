using RuntimeMetricsLab.Analysis;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Attribution.Tests;

public sealed class ReportBuilderQualityTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(3, null)]
    [InlineData(0, 0L)]
    [InlineData(3, 0L)]
    public void KernelLossEvidence_IsSeparateFromParserLossAndUnknownDoesNotMeanZero(int parserLost,
        long? kernelLost)
    {
        var fixture = SyntheticCapture.Create();
        Assert.Null(fixture.Bundle.Collector.KernelLostEvents);
        var collector = fixture.Bundle.Collector with { KernelLostEvents = kernelLost };
        var report = ReportBuilder.Build(fixture.Bundle with { Collector = collector },
            fixture.Trace with { EventsLost = parserLost });

        Assert.Equal(parserLost, report.EventsLost);
        Assert.Equal(kernelLost, report.Collector.KernelLostEvents);
        Assert.Equal(parserLost > 0, report.Quality.Any(notice => notice.Code == "event-loss"));
        Assert.Equal(parserLost > 0, report.Quality.Any(notice => notice.Code == "incomplete"));
        if (kernelLost is null)
        {
            var notice = Assert.Single(report.Quality, item => item.Code == "kernel-loss-not-reported");
            Assert.Equal("warning", notice.Severity);
            Assert.Contains("parser", notice.Explanation, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("kernel", notice.Explanation, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not proof", notice.Explanation, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.DoesNotContain(report.Quality, item => item.Code == "kernel-loss-not-reported");
        }
        if (parserLost == 0)
            CostAssert.Equal(5, SyntheticCapture.Method(report, "Arithmetic").CpuMillisecondsPerCall);
        else
            Assert.Null(SyntheticCapture.Method(report, "Arithmetic").CpuMillisecondsPerCall);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(19, false)]
    [InlineData(20, true)]
    public void PerCall_RequiresTwentySelfSamplesEvenWhenProcessCalibrationIsGood(int selfSamples, bool supported)
    {
        var fixture = SyntheticCapture.Create();
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Cpu =
            [
                .. SyntheticCapture.CpuSamples(selfSamples, [SyntheticCapture.LabFrame("Arithmetic")]),
                .. SyntheticCapture.CpuSamples(2000 - selfSamples, [SyntheticCapture.LabFrame("Checksum")])
            ],
            Allocations =
            [
                .. SyntheticCapture.AllocationSamples(selfSamples, [SyntheticCapture.LabFrame("AllocateLarge")]),
                .. SyntheticCapture.AllocationSamples(64 - selfSamples, [SyntheticCapture.LabFrame("AllocateSmall")])
            ]
        });
        var arithmetic = SyntheticCapture.Method(report, "Arithmetic");
        var large = SyntheticCapture.Method(report, "AllocateLarge");

        CostAssert.Equal(1, report.Process.CpuSamplingRatio);
        CostAssert.Equal(1, report.Process.AllocationSamplingRatio);
        CostAssert.Equal(selfSamples, arithmetic.SelfCpuMilliseconds);
        Assert.Equal((long)selfSamples, large.AllocationSamples);
        Assert.NotNull(SyntheticCapture.Method(report, "Checksum").CpuMillisecondsPerCall);
        Assert.NotNull(SyntheticCapture.Method(report, "AllocateSmall").AllocatedBytesPerCall);
        if (supported)
        {
            CostAssert.Equal(selfSamples / 300d, arithmetic.CpuMillisecondsPerCall);
            CostAssert.Equal(selfSamples * SyntheticCapture.SampleObjectSize / 4096d, large.AllocatedBytesPerCall);
        }
        else
        {
            Assert.Null(arithmetic.CpuMillisecondsPerCall);
            Assert.Null(large.AllocatedBytesPerCall);
        }
    }

    [Theory]
    [InlineData(1498, false)]
    [InlineData(1500, true)]
    [InlineData(2000, true)]
    [InlineData(2500, true)]
    [InlineData(2502, false)]
    public void CpuPerCall_UsesInclusiveTwentyFivePercentCalibrationGuardWithoutGatingAllocation(int sampleCount,
        bool supported)
    {
        var fixture = SyntheticCapture.Create();
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Cpu = SyntheticCapture.CpuSamples(sampleCount, [SyntheticCapture.LabFrame("Arithmetic")])
        });
        var arithmetic = SyntheticCapture.Method(report, "Arithmetic");

        CostAssert.Equal(sampleCount / 2000d, report.Process.CpuSamplingRatio);
        CostAssert.Equal(2000, arithmetic.SelfCpuMilliseconds);
        CostAssert.Equal(128 * 1024, SyntheticCapture.Method(report, "AllocateLarge").AllocatedBytesPerCall);
        Assert.Equal(!supported, report.Quality.Any(notice => notice.Code == "cpu-calibration"));
        if (supported)
            CostAssert.Equal(2000 / 300d, arithmetic.CpuMillisecondsPerCall);
        else
            Assert.Null(arithmetic.CpuMillisecondsPerCall);
    }

    [Theory]
    [InlineData(47, false)]
    [InlineData(48, true)]
    [InlineData(64, true)]
    [InlineData(80, true)]
    [InlineData(81, false)]
    public void AllocationPerCall_UsesIndependentInclusiveTwentyFivePercentCalibrationGuard(int sampleCount,
        bool supported)
    {
        var fixture = SyntheticCapture.Create();
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Allocations = SyntheticCapture.AllocationSamples(sampleCount, [SyntheticCapture.LabFrame("AllocateLarge")])
        });
        var large = SyntheticCapture.Method(report, "AllocateLarge");

        CostAssert.Equal(sampleCount / 64d, report.Process.AllocationSamplingRatio);
        CostAssert.Equal(5, SyntheticCapture.Method(report, "Arithmetic").CpuMillisecondsPerCall);
        Assert.Equal(!supported, report.Quality.Any(notice => notice.Code == "allocation-calibration"));
        if (supported)
            CostAssert.Equal(sampleCount * SyntheticCapture.SampleObjectSize / 4096d, large.AllocatedBytesPerCall);
        else
            Assert.Null(large.AllocatedBytesPerCall);
    }

    [Theory]
    [InlineData("window")]
    [InlineData("collector")]
    [InlineData("event-loss")]
    [InlineData("missing-attached")]
    [InlineData("late-attached")]
    public void IncompleteEvidence_WithholdsAbsoluteMethodCpuAndBothPerCallCostsButKeepsMeasuredTotals(string fault)
    {
        var fixture = SyntheticCapture.Create();
        var bundle = fixture.Bundle;
        var trace = fixture.Trace;
        switch (fault)
        {
            case "window":
                bundle = bundle with { Target = bundle.Target with { Window = bundle.Target.Window with { Complete = false } } };
                break;
            case "collector":
                bundle = bundle with { Collector = bundle.Collector with { Phase = "partial" } };
                break;
            case "event-loss":
                trace = trace with { EventsLost = 3 };
                break;
            case "missing-attached":
                trace = trace with { Markers = trace.Markers.Where(marker => marker.Payload["phase"] != "attached").ToArray() };
                break;
            case "late-attached":
                trace = trace with
                {
                    Markers = trace.Markers.Select(marker => marker.Payload["phase"] == "attached"
                        ? marker with { Milliseconds = SyntheticCapture.TraceStartMilliseconds } : marker).ToArray()
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }
        var report = ReportBuilder.Build(bundle, trace);

        CostAssert.Equal(2, report.Process.CpuSeconds);
        CostAssert.Equal(0.5, report.Process.AverageCores);
        CostAssert.Equal(0.5, report.Process.UncoveredCores);
        CostAssert.Equal(512 * SyntheticCapture.MiB, report.Process.AllocatedBytes);
        Assert.Equal(512 * SyntheticCapture.MiB, report.Methods.Sum(method => method.AllocatedBytes));
        Assert.Equal(2000, report.Methods.Sum(method => method.CpuSamples));
        Assert.All(report.Methods, method =>
        {
            Assert.Null(method.SelfCpuMilliseconds);
            Assert.Null(method.InclusiveCpuMilliseconds);
            Assert.Null(method.SelfAverageCores);
            Assert.Null(method.InclusiveAverageCores);
            Assert.Null(method.ProcessCpuShare);
            Assert.Null(method.CompletedCalls);
            Assert.Null(method.CpuMillisecondsPerCall);
            Assert.Null(method.AllocatedBytesPerCall);
        });
        Assert.Contains(report.Quality, notice => notice.Code == "incomplete" && notice.Severity == "error");
        Assert.Equal(fault == "event-loss" ? 3 : 0, report.EventsLost);
        Assert.Equal(fault == "event-loss", report.Quality.Any(notice => notice.Code == "event-loss"));
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(-1L, -1L)]
    [InlineData(301L, 300L)]
    [InlineData(299L, 300L)]
    public void InvalidOrMismatchedCounts_WithholdPerCallWithoutDiscardingAttributedCost(long entered, long completed)
    {
        var fixture = SyntheticCapture.Create();
        var target = fixture.Bundle.Target with
        {
            Calls = fixture.Bundle.Target.Calls.Select(calls => calls with { Entered = entered, Completed = completed }).ToArray()
        };
        var report = ReportBuilder.Build(fixture.Bundle with { Target = target }, fixture.Trace);

        CostAssert.Equal(1500, SyntheticCapture.Method(report, "Arithmetic").SelfCpuMilliseconds);
        Assert.Equal(512 * SyntheticCapture.MiB, SyntheticCapture.Method(report, "AllocateLarge").AllocatedBytes);
        Assert.All(report.Methods, method =>
        {
            Assert.Null(method.CompletedCalls);
            Assert.Null(method.CpuMillisecondsPerCall);
            Assert.Null(method.AllocatedBytesPerCall);
        });
        Assert.DoesNotContain(report.Quality, notice => notice.Code == "incomplete");
    }

    [Fact]
    public void MissingInvocationCounts_AreNotInferredFromSamplesOrOtherLabMethods()
    {
        var fixture = SyntheticCapture.Create();
        var report = ReportBuilder.Build(fixture.Bundle with
        {
            Target = fixture.Bundle.Target with { Calls = [] }
        }, fixture.Trace);

        CostAssert.Equal(1500, SyntheticCapture.Method(report, "Arithmetic").SelfCpuMilliseconds);
        Assert.Equal(512 * SyntheticCapture.MiB, SyntheticCapture.Method(report, "AllocateLarge").AllocatedBytes);
        Assert.All(report.Methods, method =>
        {
            Assert.Null(method.CompletedCalls);
            Assert.Null(method.CpuMillisecondsPerCall);
            Assert.Null(method.AllocatedBytesPerCall);
            Assert.Null(method.SameThreadAllocatedBytes);
        });
    }

    [Theory]
    [InlineData("user", false)]
    [InlineData("user", true)]
    [InlineData("system", false)]
    [InlineData("system", true)]
    [InlineData("allocation", false)]
    [InlineData("allocation", true)]
    public void MissingOrResetRuntimeCounter_DoesNotUseIndependentMeasurementAsFallback(string counter, bool reset)
    {
        var fixture = SyntheticCapture.Create();
        var window = fixture.Bundle.Target.Window;
        var name = counter == "allocation" ? "dotnet.gc.heap.total_allocated" : "dotnet.process.cpu.time";
        bool Matches(RawRuntimeValue value) => value.Name == name &&
            (counter == "allocation" || value.Tags.GetValueOrDefault("cpu.mode") == counter);
        var original = Assert.Single(window.Start.Values, Matches);
        var values = window.End.Values.Where(value => !Matches(value)).ToList();
        if (reset)
            values.Add(original with { Value = original.Value - 1 });
        window = window with { End = window.End with { Values = values.ToArray() } };
        var target = fixture.Bundle.Target with
        {
            Window = window,
            Samples = [window.Start, window.End],
            IndependentCpuSeconds = 123,
            IndependentAllocatedBytes = 999 * SyntheticCapture.MiB
        };
        var report = ReportBuilder.Build(fixture.Bundle with { Target = target }, fixture.Trace);

        Assert.Contains(report.Quality, notice => notice.Code == "missing-counter");
        if (counter == "allocation")
        {
            Assert.Null(report.Process.AllocatedBytes);
            Assert.Null(report.Process.AllocationBytesPerSecond);
            Assert.Null(report.Process.AllocationSamplingRatio);
            Assert.Equal(512 * SyntheticCapture.MiB, SyntheticCapture.Method(report, "AllocateLarge").AllocatedBytes);
            Assert.Null(SyntheticCapture.Method(report, "AllocateLarge").AllocatedBytesPerCall);
            CostAssert.Equal(2, report.Process.CpuSeconds);
            CostAssert.Equal(5, SyntheticCapture.Method(report, "Arithmetic").CpuMillisecondsPerCall);
        }
        else
        {
            Assert.Null(report.Process.CpuSeconds);
            Assert.Null(report.Process.AverageCores);
            Assert.Null(SyntheticCapture.Method(report, "Arithmetic").SelfCpuMilliseconds);
            Assert.Null(SyntheticCapture.Method(report, "Arithmetic").CpuMillisecondsPerCall);
            CostAssert.Equal(512 * SyntheticCapture.MiB, report.Process.AllocatedBytes);
            CostAssert.Equal(128 * 1024, SyntheticCapture.Method(report, "AllocateLarge").AllocatedBytesPerCall);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void MissingOrInvalidNominalSamplingInterval_WithholdsCpuCostsButNotAllocation(string? interval)
    {
        var fixture = SyntheticCapture.Create();
        var headers = new Dictionary<string, string>(fixture.Trace.Headers);
        if (interval is null)
            headers.Remove("ExpectedCPUSamplingRate");
        else
            headers["ExpectedCPUSamplingRate"] = interval;
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with { Headers = headers });

        CostAssert.Equal(2, report.Process.CpuSeconds);
        CostAssert.Equal(0.5, report.Process.UncoveredCores);
        Assert.Null(report.Process.NominalSampledCpuSeconds);
        Assert.Null(report.Process.CpuSamplingRatio);
        Assert.Null(SyntheticCapture.Method(report, "Arithmetic").SelfCpuMilliseconds);
        Assert.Null(SyntheticCapture.Method(report, "Arithmetic").CpuMillisecondsPerCall);
        CostAssert.Equal(128 * 1024, SyntheticCapture.Method(report, "AllocateLarge").AllocatedBytesPerCall);
        Assert.Contains(report.Quality, notice => notice.Code == "cpu-calibration");
    }

    [Fact]
    public void NoCpuObservations_LeavesAllMeasuredCpuUncoveredRatherThanInventingZeroMethodCosts()
    {
        var fixture = SyntheticCapture.Create();
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with { Cpu = [] });

        CostAssert.Equal(2, report.Process.CpuSeconds);
        CostAssert.Equal(0.5, report.Process.UncoveredCores);
        CostAssert.Equal(0, report.Process.NominalSampledCpuSeconds);
        Assert.All(report.Methods, method =>
        {
            Assert.Null(method.SelfCpuMilliseconds);
            Assert.Null(method.CpuMillisecondsPerCall);
        });
        CostAssert.Equal(128 * 1024, SyntheticCapture.Method(report, "AllocateLarge").AllocatedBytesPerCall);
        Assert.Contains(report.Quality, notice => notice.Code == "sparse-cpu");
    }

    [Fact]
    public void NoAllocationObservations_RetainsCounterBytesButWithholdsAllocationPerCall()
    {
        var fixture = SyntheticCapture.Create();
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with { Allocations = [] });

        CostAssert.Equal(512 * SyntheticCapture.MiB, report.Process.AllocatedBytes);
        CostAssert.Equal(0, report.Process.AllocationSamplingRatio);
        CostAssert.Equal(0, report.Tree.AllocationBytes);
        Assert.All(report.Methods, method => Assert.Null(method.AllocatedBytesPerCall));
        CostAssert.Equal(5, SyntheticCapture.Method(report, "Arithmetic").CpuMillisecondsPerCall);
        Assert.Contains(report.Quality, notice => notice.Code == "allocation-calibration");
    }
}
