namespace RuntimeMetricsLab.Contracts.Runs;

/// <summary>
/// Start request body. Either reference a preset by id, or supply explicit parameters, or both
/// (explicit parameters override the preset's values field-by-field). Leaving everything empty
/// uses the catalog defaults for the target experiment.
/// </summary>
public sealed record StartExperimentRequest(string? PresetId, ExperimentParameters? Parameters);
