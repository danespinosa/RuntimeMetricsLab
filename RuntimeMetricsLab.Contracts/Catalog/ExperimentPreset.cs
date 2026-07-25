namespace RuntimeMetricsLab.Contracts.Catalog;

/// <summary>A named, bounded parameter combination that a client can start without guessing values.</summary>
public sealed record ExperimentPreset(
    string Id,
    string Name,
    string Description,
    Runs.ExperimentParameters Parameters);
