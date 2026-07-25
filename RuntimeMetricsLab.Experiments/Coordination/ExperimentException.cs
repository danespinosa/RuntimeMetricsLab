using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Contracts.Errors;

namespace RuntimeMetricsLab.Experiments.Coordination;

/// <summary>Base type for every error the coordinator can raise, each carrying a stable <see cref="ExperimentErrorCode"/>.</summary>
public abstract class ExperimentException(ExperimentErrorCode errorCode, string message) : Exception(message)
{
    public ExperimentErrorCode ErrorCode { get; } = errorCode;
}

public sealed class ExperimentNotFoundException(ExperimentId id)
    : ExperimentException(ExperimentErrorCode.ExperimentNotFound, $"Experiment '{id}' was not found in the catalog.")
{
    public ExperimentId ExperimentId { get; } = id;
}

public sealed class PresetNotFoundException(ExperimentId id, string presetId)
    : ExperimentException(ExperimentErrorCode.PresetNotFound, $"Preset '{presetId}' was not found for experiment '{id}'.")
{
    public ExperimentId ExperimentId { get; } = id;
    public string PresetId { get; } = presetId;
}

public sealed class ExperimentRunAlreadyActiveException(ExperimentId id)
    : ExperimentException(ExperimentErrorCode.RunAlreadyActive, $"Experiment '{id}' already has an active run.")
{
    public ExperimentId ExperimentId { get; } = id;
}

public sealed class ExperimentCoolingDownException(ExperimentId id, DateTimeOffset cooldownUntil)
    : ExperimentException(ExperimentErrorCode.CooldownActive, $"Experiment '{id}' is cooling down until {cooldownUntil:O}.")
{
    public ExperimentId ExperimentId { get; } = id;
    public DateTimeOffset CooldownUntil { get; } = cooldownUntil;
}

public sealed class ExperimentValidationException(ExperimentId id, IReadOnlyList<string> errors)
    : ExperimentException(ExperimentErrorCode.InvalidParameters, $"Experiment '{id}' start request failed validation: {string.Join("; ", errors)}")
{
    public ExperimentId ExperimentId { get; } = id;
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class ExperimentNotRunningException(ExperimentId id)
    : ExperimentException(ExperimentErrorCode.NoActiveRun, $"Experiment '{id}' does not have an active run to cancel.")
{
    public ExperimentId ExperimentId { get; } = id;
}
