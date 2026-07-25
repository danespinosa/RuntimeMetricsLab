using System.Text.Json.Serialization;

namespace RuntimeMetricsLab.Contracts.Errors;

/// <summary>
/// Stable machine-readable error identifiers surfaced in <c>ProblemDetails.Extensions["errorCode"]</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExperimentErrorCode>))]
public enum ExperimentErrorCode
{
    ExperimentNotFound,
    PresetNotFound,
    RunAlreadyActive,
    CooldownActive,
    InvalidParameters,
    NoActiveRun,
}
