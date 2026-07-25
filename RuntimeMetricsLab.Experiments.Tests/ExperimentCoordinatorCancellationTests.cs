using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Contracts.Runs;
using RuntimeMetricsLab.Experiments.Coordination;
using RuntimeMetricsLab.Experiments.Tests.Support;

namespace RuntimeMetricsLab.Experiments.Tests;

/// <summary>Covers cooperative cancellation: cancel requests, propagation to workloads, and cleanup.</summary>
public sealed class ExperimentCoordinatorCancellationTests
{
    [Fact]
    public void Cancel_WhenNoRunIsActive_ThrowsExperimentNotRunningException()
    {
        using var harness = new TestCoordinatorHarness();

        var ex = Assert.Throws<ExperimentNotRunningException>(() => harness.Coordinator.Cancel(ExperimentId.CpuBurn));
        Assert.Equal(ExperimentId.CpuBurn, ex.ExperimentId);
    }

    [Fact]
    public void Cancel_ForUnknownExperimentId_ThrowsExperimentNotFoundException()
    {
        using var harness = new TestCoordinatorHarness();
        var unknownId = (ExperimentId)(-1);

        Assert.Throws<ExperimentNotFoundException>(() => harness.Coordinator.Cancel(unknownId));
    }

    [Fact]
    public void Cancel_WhileRunning_ImmediatelyReflectsCancellingStatus()
    {
        using var harness = new TestCoordinatorHarness();
        var gate = new TaskCompletionSource();
        harness.WorkloadFor(ExperimentId.CpuBurn).Execute = (_, ct) => gate.Task.WaitAsync(ct);

        harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(null, null));
        var snapshot = harness.Coordinator.Cancel(ExperimentId.CpuBurn);

        Assert.Equal(ExperimentRunStatus.Cancelling, snapshot.Status);
    }

    [Fact]
    public async Task Cancel_WhileRunning_PropagatesCancellationTokenToWorkload_AndReachesCancelledStatus()
    {
        using var harness = new TestCoordinatorHarness();
        var observedCancellation = new TaskCompletionSource<bool>();
        harness.WorkloadFor(ExperimentId.CpuBurn).Execute = async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                observedCancellation.TrySetResult(true);
                throw;
            }
        };

        harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(null, null));
        harness.Coordinator.Cancel(ExperimentId.CpuBurn);

        var wasCancelled = await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(wasCancelled);

        var snapshot = await harness.WaitForStatusAsync(ExperimentId.CpuBurn, s => s != ExperimentRunStatus.Running && s != ExperimentRunStatus.Cancelling);
        Assert.Equal(ExperimentRunStatus.Cancelled, snapshot.Status);
    }

    [Fact]
    public void Cancel_AfterAlreadyCancelling_ThrowsExperimentNotRunningException()
    {
        using var harness = new TestCoordinatorHarness();
        var gate = new TaskCompletionSource();
        harness.WorkloadFor(ExperimentId.CpuBurn).Execute = (_, ct) => gate.Task.WaitAsync(ct);

        harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(null, null));
        harness.Coordinator.Cancel(ExperimentId.CpuBurn);

        // Status is now Cancelling, not Running, so a second cancel is rejected.
        Assert.Throws<ExperimentNotRunningException>(() => harness.Coordinator.Cancel(ExperimentId.CpuBurn));
    }

    [Fact]
    public async Task Start_AfterPreviousRunWasCancelled_SucceedsOnceCooldownElapses()
    {
        var cooldown = TimeSpan.FromMilliseconds(200);
        using var harness = new TestCoordinatorHarness(cooldown);
        harness.WorkloadFor(ExperimentId.CpuBurn).Execute = async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { throw; }
        };

        harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(null, null));
        harness.Coordinator.Cancel(ExperimentId.CpuBurn);
        await harness.WaitForStatusAsync(ExperimentId.CpuBurn, s => s == ExperimentRunStatus.CoolingDown);

        Assert.Throws<ExperimentCoolingDownException>(
            () => harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(null, null)));

        harness.TimeProvider.Advance(cooldown + TimeSpan.FromMilliseconds(50));

        var snapshot = harness.Coordinator.Start(ExperimentId.CpuBurn, new StartExperimentRequest(null, null));
        Assert.Equal(ExperimentRunStatus.Running, snapshot.Status);
    }
}
