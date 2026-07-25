namespace RuntimeMetricsLab.Experiments.Coordination;

/// <summary>Tunables for <see cref="ExperimentCoordinator"/> that a host may want to override (e.g. shorter cooldowns in tests).</summary>
public sealed class ExperimentCoordinatorOptions
{
    /// <summary>How long an experiment stays blocked from restarting after a run reaches a terminal state.</summary>
    public TimeSpan Cooldown { get; set; } = Catalog.ExperimentLimits.DefaultCooldown;
}
