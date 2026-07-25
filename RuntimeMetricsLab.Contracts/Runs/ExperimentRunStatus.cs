using System.Text.Json.Serialization;

namespace RuntimeMetricsLab.Contracts.Runs;

/// <summary>Lifecycle state of a single experiment run, as observed by API clients.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExperimentRunStatus>))]
public enum ExperimentRunStatus
{
    /// <summary>No run has ever been started for this experiment, or the previous run's cooldown has elapsed.</summary>
    Idle,
    Running,
    Cancelling,
    Cancelled,
    Completed,
    Failed,

    /// <summary>The run finished (any terminal state) and the experiment is temporarily blocked from restarting.</summary>
    CoolingDown,
}
