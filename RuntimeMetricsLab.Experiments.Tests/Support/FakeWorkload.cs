using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Abstractions;

namespace RuntimeMetricsLab.Experiments.Tests.Support;

/// <summary>
/// A controllable <see cref="IExperimentWorkload"/> stand-in used to drive coordinator tests
/// deterministically, without depending on real CPU/memory/HTTP/gRPC behavior. Tests set
/// <see cref="Execute"/> to script exactly how/when a run completes, throws, or observes
/// cancellation.
/// </summary>
internal sealed class FakeWorkload(ExperimentId id) : IExperimentWorkload
{
    public ExperimentId Id { get; } = id;

    public Func<ExperimentRunContext, CancellationToken, Task> Execute { get; set; } = static (_, _) => Task.CompletedTask;

    public Task ExecuteAsync(ExperimentRunContext context, CancellationToken cancellationToken) =>
        Execute(context, cancellationToken);
}
