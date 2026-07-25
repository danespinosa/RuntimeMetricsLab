namespace RuntimeMetricsLab.Experiments.Workloads;

/// <summary>Small helper shared by workloads that repeat an iteration until a wall-clock deadline or cancellation.</summary>
internal static class WorkloadDurationLoop
{
    public static async Task RunAsync(TimeSpan duration, CancellationToken cancellationToken, Func<CancellationToken, Task> iteration)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(duration);

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                await iteration(timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Deadline elapsed naturally; this is a normal, successful end of the run, not a failure.
        }
    }
}
