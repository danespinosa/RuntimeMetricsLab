using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Contracts.Runs;
using RuntimeMetricsLab.Experiments.Tests.Support;

namespace RuntimeMetricsLab.Experiments.Tests;

/// <summary>Covers the run lifecycle: idle -> running -> a terminal status, for both successful and failing workloads.</summary>
public sealed class ExperimentCoordinatorLifecycleTests
{
    [Fact]
    public void GetStatus_ForExperimentThatNeverRan_ReturnsIdleWithNoRunId()
    {
        using var harness = new TestCoordinatorHarness();

        var snapshot = harness.Coordinator.GetStatus(ExperimentId.CpuBurn);

        Assert.Equal(ExperimentRunStatus.Idle, snapshot.Status);
        Assert.Null(snapshot.RunId);
        Assert.Null(snapshot.StartedAt);
        Assert.Null(snapshot.CompletedAt);
    }

    [Fact]
    public void Start_ReturnsRunningSnapshotWithRunId()
    {
        using var harness = new TestCoordinatorHarness();
        var gate = new TaskCompletionSource();
        harness.WorkloadFor(ExperimentId.CpuBurn).Execute = (_, _) => gate.Task;

        var snapshot = harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(null, null));

        Assert.Equal(ExperimentRunStatus.Running, snapshot.Status);
        Assert.NotNull(snapshot.RunId);
        Assert.NotNull(snapshot.StartedAt);

        gate.SetResult();
    }

    [Fact]
    public async Task Start_WhenWorkloadCompletesSuccessfully_TransitionsToCompleted()
    {
        using var harness = new TestCoordinatorHarness();
        var gate = new TaskCompletionSource();
        harness.WorkloadFor(ExperimentId.CpuBurn).Execute = (_, _) => gate.Task;

        harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(null, null));
        gate.SetResult();

        var snapshot = await harness.WaitForStatusAsync(ExperimentId.CpuBurn, s => s != ExperimentRunStatus.Running);

        Assert.Equal(ExperimentRunStatus.Completed, snapshot.Status);
        Assert.NotNull(snapshot.CompletedAt);
        Assert.Null(snapshot.ErrorMessage);
    }

    [Fact]
    public async Task Start_WhenWorkloadThrows_TransitionsToFailedWithErrorMessage()
    {
        using var harness = new TestCoordinatorHarness();
        harness.WorkloadFor(ExperimentId.CpuBurn).Execute = (_, _) => throw new InvalidOperationException("boom");

        harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(null, null));

        var snapshot = await harness.WaitForStatusAsync(ExperimentId.CpuBurn, s => s != ExperimentRunStatus.Running);

        Assert.Equal(ExperimentRunStatus.Failed, snapshot.Status);
        Assert.Equal("boom", snapshot.ErrorMessage);
    }

    [Fact]
    public async Task Start_ResolvesPresetDefaults_IntoRunParameters()
    {
        using var harness = new TestCoordinatorHarness();
        var gate = new TaskCompletionSource();
        harness.WorkloadFor(ExperimentId.CpuBurn).Execute = (_, _) => gate.Task;
        var preset = harness.Catalog.Get(ExperimentId.CpuBurn).Presets[0];

        var snapshot = harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(preset.Id, null));

        Assert.Equal(preset.Parameters.DurationSeconds, snapshot.Parameters?.DurationSeconds);
        Assert.Equal(preset.Parameters.IntensityPercent, snapshot.Parameters?.IntensityPercent);
        Assert.Equal(preset.Parameters.WorkerCount, snapshot.Parameters?.WorkerCount);

        gate.SetResult();
        await harness.WaitForStatusAsync(ExperimentId.CpuBurn, s => s != ExperimentRunStatus.Running);
    }

    [Fact]
    public async Task CompletedRun_ReturnsPerRunMetricsAndRemainsAddressableByRunId()
    {
        using var harness = new TestCoordinatorHarness();
        harness.WorkloadFor(ExperimentId.CpuBurn).Execute = (context, _) =>
        {
            context.Metrics.WorkIterations(42);
            return Task.CompletedTask;
        };

        var started = harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(null, null));
        var completed = await harness.WaitForStatusAsync(ExperimentId.CpuBurn, s => s != ExperimentRunStatus.Running);

        Assert.Contains(completed.Metrics, metric => metric.Name == "run.duration");
        Assert.Contains(completed.Metrics, metric => metric.Name == "work.iterations" && metric.Value == 42);
        Assert.True(harness.Coordinator.TryGetRun(started.RunId!.Value, out var historical));
        Assert.Equal(started.RunId, historical.RunId);
        Assert.Contains(historical.Metrics, metric => metric.Name == "work.iterations" && metric.Value == 42);
    }

    [Fact]
    public async Task Start_WithExplicitParameterOverride_TakesPrecedenceOverPreset()
    {
        using var harness = new TestCoordinatorHarness();
        var gate = new TaskCompletionSource();
        harness.WorkloadFor(ExperimentId.CpuBurn).Execute = (_, _) => gate.Task;
        var preset = harness.Catalog.Get(ExperimentId.CpuBurn).Presets[0];

        var snapshot = harness.Coordinator.Start(
            ExperimentId.CpuBurn,
            new StartExperimentRequest(preset.Id, new ExperimentParameters { WorkerCount = 1 }));

        Assert.Equal(1, snapshot.Parameters?.WorkerCount);
        Assert.Equal(preset.Parameters.DurationSeconds, snapshot.Parameters?.DurationSeconds);

        gate.SetResult();
        await harness.WaitForStatusAsync(ExperimentId.CpuBurn, s => s != ExperimentRunStatus.Running);
    }
}
