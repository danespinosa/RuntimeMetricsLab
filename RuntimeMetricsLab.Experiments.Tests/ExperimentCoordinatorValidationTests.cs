using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Contracts.Runs;
using RuntimeMetricsLab.Experiments.Coordination;
using RuntimeMetricsLab.Experiments.Tests.Support;

namespace RuntimeMetricsLab.Experiments.Tests;

/// <summary>Covers request validation: unknown experiments/presets, out-of-bounds parameters, and conflicting starts.</summary>
public sealed class ExperimentCoordinatorValidationTests
{
    [Fact]
    public void GetStatus_ForUnknownExperimentId_ThrowsExperimentNotFoundException()
    {
        using var harness = new TestCoordinatorHarness();
        var unknownId = (ExperimentId)(-1);

        var ex = Assert.Throws<ExperimentNotFoundException>(() => harness.Coordinator.GetStatus(unknownId));
        Assert.Equal(unknownId, ex.ExperimentId);
    }

    [Fact]
    public void Start_ForUnknownExperimentId_ThrowsExperimentNotFoundException()
    {
        using var harness = new TestCoordinatorHarness();
        var unknownId = (ExperimentId)(-1);

        Assert.Throws<ExperimentNotFoundException>(
            () => harness.Coordinator.Start(unknownId, new StartExperimentRequest(null, null)));
    }

    [Fact]
    public void Start_WithUnknownPresetId_ThrowsPresetNotFoundException()
    {
        using var harness = new TestCoordinatorHarness();

        var ex = Assert.Throws<PresetNotFoundException>(
            () => harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest("does-not-exist", null)));

        Assert.Equal(ExperimentId.CpuBurn, ex.ExperimentId);
        Assert.Equal("does-not-exist", ex.PresetId);
    }

    [Fact]
    public void Start_WithOutOfRangeExplicitParameter_ThrowsExperimentValidationExceptionWithDetails()
    {
        using var harness = new TestCoordinatorHarness();

        var ex = Assert.Throws<ExperimentValidationException>(() => harness.Coordinator.Start(
            ExperimentId.CpuBurn,
            new StartExperimentRequest(null, new ExperimentParameters { IntensityPercent = 1_000 })));

        Assert.Equal(ExperimentId.CpuBurn, ex.ExperimentId);
        Assert.Contains(ex.Errors, e => e.Contains("intensityPercent"));
    }

    [Fact]
    public void Start_WithOutOfRangeDuration_ThrowsExperimentValidationException()
    {
        using var harness = new TestCoordinatorHarness();

        var ex = Assert.Throws<ExperimentValidationException>(() => harness.Coordinator.Start(
            ExperimentId.CpuBurn,
            new StartExperimentRequest(null, new ExperimentParameters { DurationSeconds = 100_000 })));

        Assert.Contains(ex.Errors, e => e.Contains("durationSeconds"));
    }

    [Fact]
    public void Start_InvalidRequest_DoesNotDisturbPreviouslyIdleExperiment()
    {
        using var harness = new TestCoordinatorHarness();

        Assert.Throws<ExperimentValidationException>(() => harness.Coordinator.Start(
            ExperimentId.CpuBurn,
            new StartExperimentRequest(null, new ExperimentParameters { IntensityPercent = 1_000 })));

        var snapshot = harness.Coordinator.GetStatus(ExperimentId.CpuBurn);
        Assert.Equal(ExperimentRunStatus.Idle, snapshot.Status);
        Assert.Null(snapshot.RunId);
    }

    [Fact]
    public void Start_WhileAlreadyRunning_ThrowsExperimentRunAlreadyActiveException()
    {
        using var harness = new TestCoordinatorHarness();
        var gate = new TaskCompletionSource();
        harness.WorkloadFor(ExperimentId.CpuBurn).Execute = (_, _) => gate.Task;

        harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(null, null));

        var ex = Assert.Throws<ExperimentRunAlreadyActiveException>(
            () => harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(null, null)));
        Assert.Equal(ExperimentId.CpuBurn, ex.ExperimentId);

        gate.SetResult();
    }

    [Fact]
    public void Start_ForOneExperiment_DoesNotBlockStartingADifferentExperiment()
    {
        using var harness = new TestCoordinatorHarness();
        var gate = new TaskCompletionSource();
        var otherGate = new TaskCompletionSource();
        harness.WorkloadFor(ExperimentId.CpuBurn).Execute = (_, _) => gate.Task;
        harness.WorkloadFor(ExperimentId.MemoryAllocationChurn).Execute = (_, _) => otherGate.Task;

        harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(null, null));
        var otherSnapshot = harness.Coordinator.Start(ExperimentId.MemoryAllocationChurn, new StartExperimentRequest(null, null));

        Assert.Equal(ExperimentRunStatus.Running, otherSnapshot.Status);

        gate.SetResult();
        otherGate.SetResult();
    }
}
