namespace RuntimeMetricsLab.Contracts.Catalog;

/// <summary>
/// Static metadata describing one experiment: what it does, the risk it carries, the bounded
/// knobs it accepts, and the presets offered as safe starting points.
/// </summary>
public sealed record ExperimentCatalogEntry(
    ExperimentId Id,
    string Name,
    ExperimentCategory Category,
    string Description,
    string RiskNotes,
    ParameterBound Duration,
    IReadOnlyDictionary<string, ParameterBound> Parameters,
    IReadOnlyList<ExperimentPreset> Presets);
