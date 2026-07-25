using System.Diagnostics;

namespace RuntimeMetricsLab.Experiments.Metrics;

/// <summary>
/// The lab's custom <see cref="ActivitySource"/>, sharing its name with <see cref="ExperimentMetrics.MeterName"/>
/// so a single "RuntimeMetricsLab.Experiments" name identifies both the metrics and tracing signals
/// this project produces. RuntimeMetricsLab.ServiceDefaults already calls
/// <c>tracing.AddSource("RuntimeMetricsLab.Experiments")</c>, so any activity started here is
/// automatically exported.
/// </summary>
public static class ExperimentActivitySource
{
    public const string Name = ExperimentMetrics.MeterName;

    public static readonly ActivitySource Instance = new(Name, "1.0.0");
}
