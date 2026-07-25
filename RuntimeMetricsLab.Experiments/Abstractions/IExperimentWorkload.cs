using RuntimeMetricsLab.Contracts.Catalog;

namespace RuntimeMetricsLab.Experiments.Abstractions;

/// <summary>
/// A single runnable experiment. Implementations must honor <paramref name="cancellationToken"/>
/// promptly (check frequently, avoid uninterruptible blocking calls) and must release any
/// resources they acquire before returning, including when cancelled or when throwing.
/// </summary>
public interface IExperimentWorkload
{
    ExperimentId Id { get; }

    Task ExecuteAsync(ExperimentRunContext context, CancellationToken cancellationToken);
}
