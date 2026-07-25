using Microsoft.Extensions.Logging;
using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Contracts.Runs;
using RuntimeMetricsLab.Experiments.Metrics;

namespace RuntimeMetricsLab.Experiments.Abstractions;

/// <summary>Everything a workload needs to execute one run, without any coupling to the coordinator.</summary>
public sealed class ExperimentRunContext(
    Guid runId,
    ExperimentId experimentId,
    ExperimentParameters parameters,
    IExperimentMetricsRecorder metrics,
    ILogger logger)
{
    public Guid RunId { get; } = runId;

    public ExperimentId ExperimentId { get; } = experimentId;

    public ExperimentParameters Parameters { get; } = parameters;

    public IExperimentMetricsRecorder Metrics { get; } = metrics;

    public ILogger Logger { get; } = logger;
}
