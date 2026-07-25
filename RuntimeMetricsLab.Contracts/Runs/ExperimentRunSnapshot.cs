using RuntimeMetricsLab.Contracts.Catalog;

namespace RuntimeMetricsLab.Contracts.Runs;

/// <summary>Point-in-time view of an experiment's single active/most-recent run.</summary>
public sealed record ExperimentRunSnapshot(
    ExperimentId ExperimentId,
    Guid? RunId,
    ExperimentRunStatus Status,
    ExperimentParameters? Parameters,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CooldownUntil,
    string? ErrorMessage,
    IReadOnlyList<RunMetricSample> Metrics);
