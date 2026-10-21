using RuntimeMetricsLab.Analysis;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Attribution.Tests;

public sealed class ReportBuilderTests
{
    [Theory]
    [InlineData(2, 4, 0.5, 50, 12.5)]
    [InlineData(40, 10, 4, 400, 100)]
    [InlineData(1, 4, 0.25, 25, 6.25)]
    [InlineData(10, 4, 2.5, 250, 62.5)]
    public void ProcessCosts_UseRawCountersAndPermitFractionalAndMultipleCores(double cpuSeconds, double seconds,
        double cores, double oneCorePercent, double capacityPercent)
    {
        var report = SyntheticCapture.Create(seconds, cpuSeconds).Build();

        CostAssert.Equal(seconds, report.Process.Seconds);
        CostAssert.Equal(cpuSeconds, report.Process.CpuSeconds);
        CostAssert.Equal(cores, report.Process.AverageCores);
        CostAssert.Equal(oneCorePercent, report.Process.PercentOfOneCore);
        CostAssert.Equal(capacityPercent, report.Process.PercentOfRuntimeCapacity);
        CostAssert.Equal(cpuSeconds, report.Process.NominalSampledCpuSeconds);
        CostAssert.Equal(1, report.Process.CpuSamplingRatio);
        CostAssert.Equal(0, report.Process.UncoveredCores);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ProcessCosts_UnknownProcessorCountDoesNotInventCapacityPercent(int processorCount)
    {
        var report = SyntheticCapture.Create(processorCount: processorCount).Build();

        CostAssert.Equal(0.5, report.Process.AverageCores);
        CostAssert.Equal(50, report.Process.PercentOfOneCore);
        Assert.Null(report.Process.PercentOfRuntimeCapacity);
    }

    [Fact]
    public void CpuSplit_SeventyFiveTwentyFiveWithThreeHundredOneHundredCallsGivesFiveMillisecondsEach()
    {
        var fixture = SyntheticCapture.Create();
        var report = fixture.Build();
        var arithmetic = SyntheticCapture.Method(report, "Arithmetic");
        var checksum = SyntheticCapture.Method(report, "Checksum");

        CostAssert.Equal(1500, arithmetic.SelfCpuMilliseconds);
        CostAssert.Equal(500, checksum.SelfCpuMilliseconds);
        CostAssert.Equal(0.375, arithmetic.SelfAverageCores);
        CostAssert.Equal(0.125, checksum.SelfAverageCores);
        CostAssert.Equal(0.75, arithmetic.ProcessCpuShare);
        CostAssert.Equal(0.25, checksum.ProcessCpuShare);
        Assert.Equal(300L, arithmetic.CompletedCalls);
        Assert.Equal(100L, checksum.CompletedCalls);
        CostAssert.Equal(5, arithmetic.CpuMillisecondsPerCall);
        CostAssert.Equal(5, checksum.CpuMillisecondsPerCall);
        Assert.DoesNotContain(report.Quality, notice => notice.Code is "incomplete" or "cpu-calibration" or "allocation-calibration");
        Assert.Equal(fixture.Bundle.JobId, report.JobId);
        Assert.Equal(fixture.Bundle.Target.Identity, report.Identity);
        Assert.Equal(fixture.Bundle.Target.Window, report.Window);
        Assert.Equal("1000000", report.TraceHeaders["ExpectedCPUSamplingRate"]);
    }

    [Fact]
    public void QuarterShareOfFourProcessCores_IsOneMethodCore()
    {
        var report = SyntheticCapture.Create(seconds: 10, cpuSeconds: 40).Build();
        var checksum = SyntheticCapture.Method(report, "Checksum");

        CostAssert.Equal(4, report.Process.AverageCores);
        CostAssert.Equal(0.25, checksum.ProcessCpuShare);
        CostAssert.Equal(10000, checksum.SelfCpuMilliseconds);
        CostAssert.Equal(1, checksum.SelfAverageCores);
    }

    [Fact]
    public void AllocationTotals_512MiBOverTenSecondsAnd4096CallsAreIndependentByteCosts()
    {
        var report = SyntheticCapture.Create(seconds: 10).Build();
        var allocation = SyntheticCapture.Method(report, "AllocateLarge");

        CostAssert.Equal(512 * SyntheticCapture.MiB, report.Process.AllocatedBytes);
        CostAssert.Equal(51.2, report.Process.AllocationBytesPerSecond / SyntheticCapture.MiB);
        CostAssert.Equal(1, report.Process.AllocationSamplingRatio);
        Assert.Equal(512 * SyntheticCapture.MiB, allocation.AllocatedBytes);
        Assert.Equal(51.2, allocation.AllocationBytesPerSecond / SyntheticCapture.MiB, 10);
        Assert.Equal(64L, allocation.AllocationSamples);
        Assert.Equal(4096L, allocation.CompletedCalls);
        CostAssert.Equal(128 * 1024, allocation.AllocatedBytesPerCall);
        Assert.Equal(512 * SyntheticCapture.MiB, allocation.SameThreadAllocatedBytes);
        Assert.Equal(512 * SyntheticCapture.MiB, report.Tree.AllocationBytes);
    }

    [Fact]
    public void AllocationOnlyMethod_HasUnknownSelfAndInclusiveCpuRatherThanZeroCpuCosts()
    {
        var report = SyntheticCapture.Create().Build();
        var allocation = SyntheticCapture.Method(report, "AllocateLarge");

        CostAssert.Equal(2, report.Process.CoveredCpuSeconds);
        CostAssert.Equal(1500, SyntheticCapture.Method(report, "Arithmetic").SelfCpuMilliseconds);
        Assert.Equal(0, allocation.CpuSamples);
        Assert.Null(allocation.SelfCpuMilliseconds);
        Assert.Null(allocation.InclusiveCpuMilliseconds);
        Assert.Null(allocation.SelfAverageCores);
        Assert.Null(allocation.InclusiveAverageCores);
        Assert.Null(allocation.ProcessCpuShare);
        Assert.Null(allocation.CpuMillisecondsPerCall);
        Assert.Equal(64L, allocation.AllocationSamples);
        Assert.Equal(512 * SyntheticCapture.MiB, allocation.AllocatedBytes);
        CostAssert.Equal(128 * 1024, allocation.AllocatedBytesPerCall);
    }

    [Fact]
    public void ChangingCpuShareCannotChangeAnyFlatAllocationCost()
    {
        var fixture = SyntheticCapture.Create();
        var before = fixture.Build();
        var after = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Cpu =
            [
                .. SyntheticCapture.CpuSamples(500, [SyntheticCapture.LabFrame("Arithmetic"), SyntheticCapture.LabFrame("MixedPipeline")]),
                .. SyntheticCapture.CpuSamples(1500, [SyntheticCapture.LabFrame("Checksum"), SyntheticCapture.LabFrame("MixedPipeline")])
            ]
        });

        CostAssert.Equal(0.75, SyntheticCapture.Method(before, "Arithmetic").ProcessCpuShare);
        CostAssert.Equal(0.25, SyntheticCapture.Method(after, "Arithmetic").ProcessCpuShare);
        Assert.Equal(before.Process.AllocatedBytes, after.Process.AllocatedBytes);
        Assert.Equal(before.Process.AllocationBytesPerSecond, after.Process.AllocationBytesPerSecond);
        Assert.Equal(before.Process.AllocationSamplingRatio, after.Process.AllocationSamplingRatio);
        Assert.Equal(before.Tree.AllocationBytes, after.Tree.AllocationBytes);
        Assert.Equal(
            before.Methods.OrderBy(method => method.Key).Select(AllocationCosts).ToArray(),
            after.Methods.OrderBy(method => method.Key).Select(AllocationCosts).ToArray());

        static object AllocationCosts(MethodCosts method) => new
        {
            method.Key, method.AllocationSamples, method.AllocatedBytes, method.InclusiveAllocatedBytes,
            method.AllocationBytesPerSecond, method.AllocatedBytesPerCall, method.SameThreadAllocatedBytes
        };
    }

    [Fact]
    public void RecursiveStacks_ConserveExclusiveCostsAndCountFlatInclusiveRecursionOnce()
    {
        var fixture = SyntheticCapture.Create();
        var arithmetic = SyntheticCapture.LabFrame("Arithmetic");
        var checksum = SyntheticCapture.LabFrame("Checksum");
        var large = SyntheticCapture.LabFrame("AllocateLarge");
        var small = SyntheticCapture.LabFrame("AllocateSmall");
        var pipeline = SyntheticCapture.LabFrame("MixedPipeline");
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Cpu =
            [
                .. SyntheticCapture.CpuSamples(1200, [arithmetic, arithmetic, pipeline]),
                .. SyntheticCapture.CpuSamples(800, [checksum, arithmetic, pipeline])
            ],
            Allocations =
            [
                .. SyntheticCapture.AllocationSamples(32, [large, large, pipeline]),
                .. SyntheticCapture.AllocationSamples(32, [small, large, pipeline])
            ]
        });

        CostAssert.Equal(1200, SyntheticCapture.Method(report, "Arithmetic").SelfCpuMilliseconds);
        CostAssert.Equal(2000, SyntheticCapture.Method(report, "Arithmetic").InclusiveCpuMilliseconds);
        CostAssert.Equal(800, SyntheticCapture.Method(report, "Checksum").InclusiveCpuMilliseconds);
        var pipelineCosts = SyntheticCapture.Method(report, "MixedPipeline");
        Assert.Null(pipelineCosts.SelfCpuMilliseconds);
        Assert.Null(pipelineCosts.SelfAverageCores);
        Assert.Null(pipelineCosts.ProcessCpuShare);
        Assert.Null(pipelineCosts.CpuMillisecondsPerCall);
        CostAssert.Equal(0.5, pipelineCosts.InclusiveAverageCores);
        CostAssert.Equal(2000, SyntheticCapture.Method(report, "MixedPipeline").InclusiveCpuMilliseconds);
        CostAssert.Equal(2000, report.Methods.Sum(method => method.SelfCpuMilliseconds));
        CostAssert.Equal(4800, report.Methods.Sum(method => method.InclusiveCpuMilliseconds));
        Assert.Equal(256 * SyntheticCapture.MiB, SyntheticCapture.Method(report, "AllocateLarge").AllocatedBytes);
        Assert.Equal(512 * SyntheticCapture.MiB, SyntheticCapture.Method(report, "AllocateLarge").InclusiveAllocatedBytes);
        Assert.Equal(256 * SyntheticCapture.MiB, SyntheticCapture.Method(report, "AllocateSmall").InclusiveAllocatedBytes);
        Assert.Equal(512 * SyntheticCapture.MiB, report.Methods.Sum(method => method.AllocatedBytes));
        Assert.Equal(1280 * SyntheticCapture.MiB, report.Methods.Sum(method => method.InclusiveAllocatedBytes));
        CostAssert.Equal(2000, report.Tree.CpuWeight);
        CostAssert.Equal(512 * SyntheticCapture.MiB, report.Tree.AllocationBytes);
        AssertTreeConservation(report.Tree);
        var nodes = TreeNodes(report.Tree).ToArray();
        Assert.Equal(nodes.Length, nodes.Select(node => node.Id).Distinct().Count());
        Assert.Equal(2, nodes.Count(node => node.MethodKey == SyntheticCapture.LabModule + "!" + arithmetic.Method));
    }

    [Fact]
    public void SameMethodNameInDifferentModules_HasDistinctCostsAndNoInventedForeignInvocationCount()
    {
        var fixture = SyntheticCapture.Create();
        var lab = SyntheticCapture.LabFrame("Arithmetic");
        var foreign = lab with { Module = "another.application" };
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Cpu = [.. SyntheticCapture.CpuSamples(1500, [lab]), .. SyntheticCapture.CpuSamples(500, [foreign])]
        });
        var labCosts = SyntheticCapture.Method(report, "Arithmetic");
        var foreignCosts = Assert.Single(report.Methods, method => method.Module == foreign.Module);

        Assert.Equal(labCosts.Method, foreignCosts.Method);
        Assert.NotEqual(labCosts.Key, foreignCosts.Key);
        CostAssert.Equal(1500, labCosts.SelfCpuMilliseconds);
        CostAssert.Equal(500, foreignCosts.SelfCpuMilliseconds);
        CostAssert.Equal(5, labCosts.CpuMillisecondsPerCall);
        Assert.Equal("lab", labCosts.Category);
        Assert.Equal("runtime/application", foreignCosts.Category);
        Assert.Null(foreignCosts.CompletedCalls);
        Assert.Null(foreignCosts.CpuMillisecondsPerCall);
        Assert.Null(foreignCosts.AllocatedBytesPerCall);
        Assert.Null(foreignCosts.SameThreadAllocatedBytes);
        Assert.Contains(report.Tree.Children, node => node.MethodKey == labCosts.Key);
        Assert.Contains(report.Tree.Children, node => node.MethodKey == foreignCosts.Key);
    }

    [Fact]
    public void UnknownAndUnresolvedNativeCpuBuckets_AreVisibleAndNotRedistributedToLabMethods()
    {
        var fixture = SyntheticCapture.Create();
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Cpu =
            [
                .. SyntheticCapture.CpuSamples(1000, [SyntheticCapture.LabFrame("Arithmetic")]),
                .. SyntheticCapture.CpuSamples(500, [new("", "[unknown stack]")]),
                .. SyntheticCapture.CpuSamples(250, [new("vmlinux", "[unresolved 0x1234]")]),
                .. SyntheticCapture.CpuSamples(250, [new("vmlinux", "[unresolved 0xabcd]")])
            ]
        });
        var unknown = Assert.Single(report.Methods, method => method.Category == "unknown");
        var native = Assert.Single(report.Methods, method => method.Category == "kernel");

        CostAssert.Equal(1000, SyntheticCapture.Method(report, "Arithmetic").SelfCpuMilliseconds);
        CostAssert.Equal(500, unknown.SelfCpuMilliseconds);
        CostAssert.Equal(500, native.SelfCpuMilliseconds);
        Assert.Equal("[unresolved vmlinux]", native.Method);
        CostAssert.Equal(2000, report.Methods.Sum(method => method.SelfCpuMilliseconds));
        Assert.Contains(report.Quality, notice => notice.Code == "unresolved-symbols");
        Assert.Contains(report.Tree.Children, node => node.MethodKey == unknown.Key);
        Assert.Contains(report.Tree.Children, node => node.MethodKey == native.Key);
        Assert.Null(unknown.CompletedCalls);
        Assert.Null(native.CompletedCalls);
        AssertTreeConservation(report.Tree);
    }

    [Fact]
    public void AllocationOwnership_SkipsKernelAndClrEmissionFramesBeforeFirstManagedFrame()
    {
        var report = SyntheticCapture.Create().Build();

        Assert.Equal(512 * SyntheticCapture.MiB, SyntheticCapture.Method(report, "AllocateLarge").AllocatedBytes);
        Assert.Equal(512 * SyntheticCapture.MiB, SyntheticCapture.Method(report, "MixedPipeline").InclusiveAllocatedBytes);
        Assert.DoesNotContain(report.Methods, method => method.Module is "vmlinux" or "libcoreclr.so");
        Assert.DoesNotContain(TreeNodes(report.Tree), node => node.Name is "user_events_write" or "EmitAllocationSample");
    }

    [Fact]
    public void NativeOnlyAllocationStack_UsesUnknownAllocationSiteInsteadOfDiscardingBytes()
    {
        var fixture = SyntheticCapture.Create();
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Allocations = SyntheticCapture.AllocationSamples(64,
                [new("vmlinux", "user_events_write"), new("libcoreclr.so", "EmitAllocationSample")])
        });
        var unknown = Assert.Single(report.Methods, method => method.Method == "[unknown allocation site]");

        Assert.Equal("unknown", unknown.Category);
        Assert.Equal(64L, unknown.AllocationSamples);
        Assert.Equal(512 * SyntheticCapture.MiB, unknown.AllocatedBytes);
        Assert.Equal(512 * SyntheticCapture.MiB, report.Tree.AllocationBytes);
        Assert.Equal(512 * SyntheticCapture.MiB, report.Methods.Sum(method => method.AllocatedBytes));
        Assert.Null(unknown.CompletedCalls);
        Assert.Null(unknown.AllocatedBytesPerCall);
        AssertTreeConservation(report.Tree);
    }

    [Fact]
    public void AllocationWeight_UsesObjectSizeAndNotSampledByteOffset()
    {
        var fixture = SyntheticCapture.Create();
        var samples = fixture.Trace.Allocations.Select(sample => sample with { ObjectSize = 1048600 }).ToArray();
        var before = ReportBuilder.Build(fixture.Bundle, fixture.Trace with { Allocations = samples });
        var after = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Allocations = samples.Select(sample => sample with { SampledByteOffset = 1048599 }).ToArray()
        });

        Assert.InRange(Math.Abs(SyntheticCapture.Method(before, "AllocateLarge").AllocatedBytes - 64 * 1048637.439182857472),
            0, 0.0001);
        Assert.Equal(before.Tree.AllocationBytes, after.Tree.AllocationBytes);
        Assert.Equal(SyntheticCapture.Method(before, "AllocateLarge").AllocatedBytes,
            SyntheticCapture.Method(after, "AllocateLarge").AllocatedBytes);
    }

    [Fact]
    public void ObservationWindow_IncludesStartAndExcludesEndForCpuAndAllocations()
    {
        var fixture = SyntheticCapture.Create();
        var start = SyntheticCapture.TraceStartMilliseconds;
        var end = start + 4000;
        ProfileFrame[] outside = [new("outside.window", "OutsideWindow")];
        var cpu = fixture.Trace.Cpu.ToArray();
        cpu[0] = cpu[0] with { Milliseconds = start };
        cpu[^1] = cpu[^1] with { Milliseconds = end - 0.001 };
        var allocations = fixture.Trace.Allocations.ToArray();
        allocations[0] = allocations[0] with { Milliseconds = start };
        allocations[^1] = allocations[^1] with { Milliseconds = end - 0.001 };
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Cpu = [.. cpu, cpu[0] with { Milliseconds = start - 0.001, Frames = outside }, cpu[0] with { Milliseconds = end, Frames = outside }],
            Allocations = [.. allocations, allocations[0] with { Milliseconds = start - 0.001, Frames = outside }, allocations[0] with { Milliseconds = end, Frames = outside }]
        });

        Assert.DoesNotContain(report.Methods, method => method.Module == "outside.window");
        CostAssert.Equal(2, report.Process.NominalSampledCpuSeconds);
        CostAssert.Equal(1, report.Process.CpuSamplingRatio);
        CostAssert.Equal(1, report.Process.AllocationSamplingRatio);
        CostAssert.Equal(2000, report.Methods.Sum(method => method.SelfCpuMilliseconds));
        Assert.Equal(512 * SyntheticCapture.MiB, report.Tree.AllocationBytes);
    }

    [Fact]
    public void IndependentCalibrationResiduals_AreReportedRatherThanForcedToZero()
    {
        var fixture = SyntheticCapture.Create();
        var report = ReportBuilder.Build(fixture.Bundle, fixture.Trace with
        {
            Cpu = SyntheticCapture.CpuSamples(1500, [SyntheticCapture.LabFrame("Arithmetic")]),
            Allocations = SyntheticCapture.AllocationSamples(48, [SyntheticCapture.LabFrame("AllocateLarge")])
        });

        CostAssert.Equal(2, report.Process.CpuSeconds);
        CostAssert.Equal(1.5, report.Process.NominalSampledCpuSeconds);
        CostAssert.Equal(0.75, report.Process.CpuSamplingRatio);
        CostAssert.Equal(512 * SyntheticCapture.MiB, report.Process.AllocatedBytes);
        CostAssert.Equal(0.75, report.Process.AllocationSamplingRatio);
        Assert.Equal(384 * SyntheticCapture.MiB, report.Tree.AllocationBytes);
    }

    private static void AssertTreeConservation(CallNode root)
    {
        Assert.All(TreeNodes(root), node =>
        {
            CostAssert.Equal(node.CpuWeight, node.SelfCpuWeight + node.Children.Sum(child => child.CpuWeight));
            CostAssert.Equal(node.AllocationBytes, node.SelfAllocationBytes + node.Children.Sum(child => child.AllocationBytes));
        });
    }

    private static IEnumerable<CallNode> TreeNodes(CallNode root)
    {
        yield return root;
        foreach (var child in root.Children)
        foreach (var node in TreeNodes(child))
            yield return node;
    }
}
