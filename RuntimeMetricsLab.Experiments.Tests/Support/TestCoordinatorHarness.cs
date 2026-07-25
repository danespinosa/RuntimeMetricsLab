using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Contracts.Runs;
using RuntimeMetricsLab.Experiments.Catalog;
using RuntimeMetricsLab.Experiments.Coordination;
using RuntimeMetricsLab.Experiments.Metrics;

namespace RuntimeMetricsLab.Experiments.Tests.Support;

/// <summary>
/// Wires a real <see cref="ExperimentCatalog"/> and <see cref="ExperimentMetrics"/> together with
/// one <see cref="FakeWorkload"/> per catalog entry and an <see cref="ExperimentCoordinator"/>
/// under test, so coordinator behavior can be exercised without any real CPU/memory/HTTP/gRPC
/// side effects. Defaults to zero cooldown so tests observe true terminal statuses immediately;
/// pass an explicit cooldown to exercise cooldown behavior with the bundled <see cref="ManualTimeProvider"/>.
/// </summary>
internal sealed class TestCoordinatorHarness : IDisposable
{
    public IExperimentCatalog Catalog { get; }

    public ExperimentMetrics Metrics { get; }

    public ManualTimeProvider TimeProvider { get; }

    public ExperimentCoordinator Coordinator { get; }

    public Dictionary<ExperimentId, FakeWorkload> Workloads { get; }

    public TestCoordinatorHarness(TimeSpan? cooldown = null)
    {
        Catalog = new ExperimentCatalog();
        Metrics = new ExperimentMetrics();
        TimeProvider = new ManualTimeProvider();

        Workloads = Catalog.Entries.ToDictionary(e => e.Id, e => new FakeWorkload(e.Id));

        var options = Options.Create(new ExperimentCoordinatorOptions
        {
            Cooldown = cooldown ?? TimeSpan.Zero,
        });

        Coordinator = new ExperimentCoordinator(
            Catalog,
            Workloads.Values,
            Metrics,
            NullLogger<ExperimentCoordinator>.Instance,
            options,
            TimeProvider);
    }

    public FakeWorkload WorkloadFor(ExperimentId id) => Workloads[id];

    /// <summary>Polls <c>GetStatus</c> until <paramref name="predicate"/> is satisfied or <paramref name="timeout"/> elapses.</summary>
    public async Task<ExperimentRunSnapshot> WaitForStatusAsync(
        ExperimentId id,
        Func<ExperimentRunStatus, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var snapshot = Coordinator.GetStatus(id);
            if (predicate(snapshot.Status))
            {
                return snapshot;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Experiment '{id}' never reached the expected status (last observed: {snapshot.Status}).");
            }

            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        Coordinator.Dispose();
        Metrics.Dispose();
    }
}
