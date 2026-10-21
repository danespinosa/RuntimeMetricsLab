using System.Text.Json;
using RuntimeMetricsLab.Analysis;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Attribution.Tests;

public sealed class IntegratedRegressionTests
{
    [Fact]
    public void TieredJitMissingModule_JoinsOnlyUniqueExactSignatureBeforeFullCallDenominator()
    {
        var fixture = SyntheticCapture.Create();
        var arithmetic = SyntheticCapture.LabFrame("Arithmetic");
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Cpu = [
                .. SyntheticCapture.CpuSamples(500, [arithmetic]),
                .. SyntheticCapture.CpuSamples(1000, [arithmetic with { Module = "" }]),
                .. SyntheticCapture.CpuSamples(500, [SyntheticCapture.LabFrame("Checksum")])
            ]
        });
        var method = SyntheticCapture.Method(report, "Arithmetic");
        Assert.Single(report.Methods, item => item.Method == arithmetic.Method);
        CostAssert.Equal(1500, method.SelfCpuMilliseconds);
        CostAssert.Equal(5, method.CpuMillisecondsPerCall);
        Assert.Equal(1500, method.CpuSamples);
        Assert.Contains(report.Quality, item => item.Code == "module-metadata-reconciled");
    }

    [Fact]
    public void AmbiguousMissingModule_DoesNotJoinOrUseAFullMethodCallCount()
    {
        var fixture = SyntheticCapture.Create();
        var arithmetic = SyntheticCapture.LabFrame("Arithmetic");
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Cpu = [
                .. SyntheticCapture.CpuSamples(500, [arithmetic]),
                .. SyntheticCapture.CpuSamples(500, [arithmetic with { Module = "another.module" }]),
                .. SyntheticCapture.CpuSamples(500, [arithmetic with { Module = "" }]),
                .. SyntheticCapture.CpuSamples(500, [SyntheticCapture.LabFrame("Checksum")])
            ]
        });
        var affected = report.Methods.Where(item => item.Method == arithmetic.Method).ToArray();
        Assert.Equal(new[] { "", "another.module", SyntheticCapture.LabModule },
            affected.Select(item => item.Module).OrderBy(module => module, StringComparer.Ordinal).ToArray());
        Assert.All(affected, method =>
        {
            CostAssert.Equal(500, method.SelfCpuMilliseconds);
            Assert.Null(method.CompletedCalls);
            Assert.Null(method.CpuMillisecondsPerCall);
        });
        Assert.Contains(report.Quality, item => item.Code == "ambiguous-module");
        CostAssert.Equal(2000, report.Methods.Sum(item => item.SelfCpuMilliseconds));
    }

    [Fact]
    public void AllocationModuleReconciliation_PrecedesEmissionPrefixRemoval()
    {
        var fixture = SyntheticCapture.Create();
        var large = SyntheticCapture.LabFrame("AllocateLarge");
        var samples = fixture.Trace.Allocations.Select((sample, index) => sample with
        {
            Frames = [new("vmlinux", "write_event"), new("libcoreclr.so", "EmitAllocation"),
                index == 0 ? large : large with { Module = "" }, SyntheticCapture.LabFrame("MixedPipeline")]
        }).ToArray();
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with { Allocations = samples });
        var method = SyntheticCapture.Method(report, "AllocateLarge");
        Assert.Equal(512 * SyntheticCapture.MiB, method.AllocatedBytes);
        CostAssert.Equal(128 * 1024, method.AllocatedBytesPerCall);
        Assert.Equal(0, SyntheticCapture.Method(report, "MixedPipeline").AllocatedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreconciledAllocationModule_PreservesLeafOwnershipInsteadOfSkippingToManagedAncestor(bool ambiguous)
    {
        var fixture = SyntheticCapture.Create();
        var large = SyntheticCapture.LabFrame("AllocateLarge");
        var unresolvedModule = large with { Module = "" };
        var trace = fixture.Trace with
        {
            Allocations = fixture.Trace.Allocations.Select(sample => sample with
            {
                Frames =
                [
                    new("vmlinux", "write_event"), new("libcoreclr.so", "EmitAllocation"),
                    unresolvedModule, SyntheticCapture.LabFrame("MixedPipeline")
                ]
            }).ToArray()
        };
        if (ambiguous)
        {
            trace = trace with
            {
                Cpu =
                [
                    .. SyntheticCapture.CpuSamples(1000, [SyntheticCapture.LabFrame("Arithmetic")]),
                    .. SyntheticCapture.CpuSamples(500, [large]),
                    .. SyntheticCapture.CpuSamples(500, [large with { Module = "another.module" }])
                ]
            };
        }
        var report = ReportBuilder.Build(fixture.Bundle, trace);
        var allocation = Assert.Single(report.Methods, method => method.Key == "!" + large.Method);

        Assert.Equal("", allocation.Module);
        Assert.Equal(64L, allocation.AllocationSamples);
        Assert.Equal(512 * SyntheticCapture.MiB, allocation.AllocatedBytes);
        Assert.Null(allocation.CompletedCalls);
        Assert.Null(allocation.AllocatedBytesPerCall);
        Assert.Equal(0, SyntheticCapture.Method(report, "MixedPipeline").AllocatedBytes);
        Assert.Equal(512 * SyntheticCapture.MiB, SyntheticCapture.Method(report, "MixedPipeline").InclusiveAllocatedBytes);
        Assert.Equal(512 * SyntheticCapture.MiB, report.Methods.Sum(method => method.AllocatedBytes));
        Assert.Contains(report.Quality, notice => notice.Code == "ambiguous-module");
        if (ambiguous)
        {
            Assert.Null(SyntheticCapture.Method(report, "AllocateLarge").CompletedCalls);
            Assert.Null(SyntheticCapture.Method(report, "AllocateLarge").CpuMillisecondsPerCall);
        }
    }

    [Theory]
    [InlineData(1000L, 1000L, true, 1000L)]
    [InlineData(1L, 0L, true, null)]
    [InlineData(0L, 1L, true, null)]
    [InlineData(1001L, 1000L, true, null)]
    [InlineData(1000L, 1000L, false, null)]
    public void InstrumentedWaitingMethodWithoutSamples_RemainsVisibleWithoutFreeCostClaim(long entered,
        long completed, bool complete, long? expectedCompleted)
    {
        var fixture = SyntheticCapture.Create();
        var target = fixture.Bundle.Target with
        {
            Window = fixture.Bundle.Target.Window with { Complete = complete },
            Calls = [.. fixture.Bundle.Target.Calls, new("WaitStep", entered, completed, 0)]
        };
        var report = ReportBuilder.Build(fixture.Bundle with { Target = target }, fixture.Trace);
        var waiting = Assert.Single(report.Methods, method => method.Key == "instrumented:WaitStep");
        Assert.Equal("lab", waiting.Category);
        Assert.Contains("no samples", waiting.Method, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedCompleted, waiting.CompletedCalls);
        Assert.Equal(0, waiting.CpuSamples);
        Assert.Equal(0L, waiting.AllocationSamples);
        Assert.Equal(0, waiting.AllocatedBytes);
        Assert.Equal(0, waiting.InclusiveAllocatedBytes);
        Assert.Equal(0, waiting.AllocationBytesPerSecond);
        Assert.Equal(0L, waiting.SameThreadAllocatedBytes);
        Assert.Null(waiting.SelfCpuMilliseconds);
        Assert.Null(waiting.InclusiveCpuMilliseconds);
        Assert.Null(waiting.SelfAverageCores);
        Assert.Null(waiting.InclusiveAverageCores);
        Assert.Null(waiting.ProcessCpuShare);
        Assert.Null(waiting.CpuMillisecondsPerCall);
        Assert.Null(waiting.AllocatedBytesPerCall);
        CostAssert.Equal(2, report.Process.CpuSeconds);
        CostAssert.Equal(512 * SyntheticCapture.MiB, report.Process.AllocatedBytes);
        CostAssert.Equal(2000, report.Tree.CpuWeight);
        CostAssert.Equal(512 * SyntheticCapture.MiB, report.Tree.AllocationBytes);
    }

    [Fact]
    public void PartialReport_RetainsMeasuredProcessCpuButWithholdsGraphCalibrationPool()
    {
        var fixture = SyntheticCapture.Create();
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with { EventsLost = 1 });
        CostAssert.Equal(2, report.Process.CpuSeconds);
        Assert.Null(report.Process.CoveredCpuSeconds);
        Assert.All(report.Methods, method => Assert.Null(method.SelfAverageCores));
        CostAssert.Equal(report.Process.AverageCores!.Value, report.Process.UncoveredCores);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public void CoveredCpuContract_RoundTripsNullAndZeroWithoutSubstitutingMeasuredProcessCpu(double? coveredCpuSeconds)
    {
        var report = SyntheticCapture.Create().Build();
        report = report with { Process = report.Process with { CoveredCpuSeconds = coveredCpuSeconds } };

        var json = JsonSerializer.Serialize(report, AttributionJson.ReportOptions);
        var restored = JsonSerializer.Deserialize<AttributionReport>(json, AttributionJson.ReportOptions);

        Assert.NotNull(restored);
        CostAssert.Equal(2, restored.Process.CpuSeconds);
        Assert.Equal(coveredCpuSeconds, restored.Process.CoveredCpuSeconds);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(256)]
    public void DeepSyntheticReport_RoundTripsDecoderDepthsWithSharedOptions(int frameCount)
    {
        var fixture = SyntheticCapture.Create();
        ProfileFrame[] frames =
        [
            SyntheticCapture.LabFrame("Arithmetic"),
            .. Enumerable.Range(1, frameCount - 1)
                .Select(index => new ProfileFrame("synthetic.application", $"Frame{index}()"))
        ];
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Cpu = fixture.Trace.Cpu.Select(sample => sample with { Frames = frames }).ToArray()
        });
        var revision = report with { Revision = 2 };

        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(revision));
        var json = JsonSerializer.Serialize(revision, AttributionJson.ReportOptions);
        var restored = JsonSerializer.Deserialize<AttributionReport>(json, AttributionJson.ReportOptions);

        Assert.NotNull(restored);
        Assert.Equal(1, report.Revision);
        Assert.Equal(2, restored.Revision);
        Assert.Equal(report.JobId, restored.JobId);
        Assert.Equal(report.Process, restored.Process);
        Assert.Equal(report.Methods, restored.Methods);
        Assert.DoesNotContain("\"$id\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$ref\"", json, StringComparison.Ordinal);
        CostAssert.Equal(2000, restored.Tree.CpuWeight);
        CostAssert.Equal(512 * SyntheticCapture.MiB, restored.Tree.AllocationBytes);

        var count = 1;
        var callerKey = frames[^1].Module + "!" + frames[^1].Method;
        var node = Assert.Single(restored.Tree.Children, child => child.MethodKey == callerKey);
        while (node.Children.Length > 0) { count++; node = Assert.Single(node.Children); }
        Assert.Equal(frameCount, count);
        Assert.Equal(frames[0].Module + "!" + frames[0].Method, node.MethodKey);
        CostAssert.Equal(2000, node.SelfCpuWeight);
    }

    [Theory]
    [InlineData(1, 31)]
    [InlineData(5, 35)]
    [InlineData(60, 90)]
    public void CaptureBudget_CoversDelayedEnablementWorkBaselineAndRecovery(int work, int expected)
    {
        Assert.Equal(20, AttributionBudgets.AttachmentSeconds);
        Assert.Equal(60, AttributionBudgets.MaximumWorkSeconds);
        Assert.Equal(90, AttributionBudgets.MaximumCaptureSeconds);
        Assert.Equal(expected, AttributionBudgets.CaptureSeconds(work));
        Assert.True(expected >= work + AttributionBudgets.AttachmentSeconds + 3);
        Assert.True(expected <= AttributionBudgets.MaximumCaptureSeconds);
    }

    [Fact]
    public void IntervalRates_UseRecordedTargetFrequency_NotUiHostFrequency()
    {
        var fixture = SyntheticCapture.Create(seconds: 10, processorCount: 8);
        CostAssert.Equal(0.2, AttributionMetrics.IntervalCores(fixture.Bundle.Target.Window));
        CostAssert.Equal(51.2, AttributionMetrics.IntervalAllocationRate(fixture.Bundle.Target.Window) / SyntheticCapture.MiB);
        var slowerClock = fixture.Bundle.Target.Window with { Frequency = SyntheticCapture.Frequency / 2 };
        CostAssert.Equal(0.1, AttributionMetrics.IntervalCores(slowerClock));
        CostAssert.Equal(25.6, AttributionMetrics.IntervalAllocationRate(slowerClock) / SyntheticCapture.MiB);
    }

    [Fact]
    public void IntervalRates_RejectMissingResetAndNonpositiveIntervals()
    {
        var window = SyntheticCapture.Create().Bundle.Target.Window;
        Assert.Null(AttributionMetrics.IntervalCores(window with { Frequency = 0 }));
        Assert.Null(AttributionMetrics.IntervalAllocationRate(window with { End = window.Start }));
        Assert.Null(AttributionMetrics.IntervalCores(window with { Start = window.End, End = window.Start }));
        var missing = window with { End = window.End with { Values = [] } };
        Assert.Null(AttributionMetrics.IntervalCores(missing));
        Assert.Null(AttributionMetrics.IntervalAllocationRate(missing));
    }

    [Fact]
    public void FractionalInheritedQuota_RemainsMetadata_NotInventedCpuCountOrMemoryPercentage()
    {
        var fixture = SyntheticCapture.Create(processorCount: 8);
        var identity = fixture.Bundle.Target.Identity with
        {
            Environment = new() { ["cpu.max"] = "50000 100000", ["memory.max"] = "max", ["cpuset.cpus.effective"] = "0-7" }
        };
        var report = ReportBuilder.Build(fixture.Bundle with { Target = fixture.Bundle.Target with { Identity = identity } }, fixture.Trace);
        CostAssert.Equal(0.5, report.Process.AverageCores);
        CostAssert.Equal(6.25, report.Process.PercentOfRuntimeCapacity);
        Assert.Equal("50000 100000", report.Identity.Environment["cpu.max"]);
        Assert.Equal("max", report.Identity.Environment["memory.max"]);
    }
}
