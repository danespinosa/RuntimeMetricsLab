using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Contracts.Runs;

namespace RuntimeMetricsLab.Experiments.Coordination;

/// <summary>
/// Mutable state for the single active-or-most-recent run of one experiment. Every mutation is
/// performed under <see cref="Gate"/> so start/status/cancel calls never race each other. There is
/// exactly one <see cref="RunState"/> per <see cref="ExperimentId"/>, created lazily and reused for
/// every subsequent run — the coordinator never keeps run history beyond the latest run.
/// </summary>
internal sealed class RunState
{
    public readonly object Gate = new();

    public ExperimentRunStatus Status = ExperimentRunStatus.Idle;
    public Guid? RunId;
    public ExperimentParameters? Parameters;
    public DateTimeOffset? StartedAt;
    public DateTimeOffset? CompletedAt;
    public DateTimeOffset? CooldownUntil;
    public string? ErrorMessage;
    public IReadOnlyList<RunMetricSample> Metrics = Array.Empty<RunMetricSample>();
    public CancellationTokenSource? Cts;
}
