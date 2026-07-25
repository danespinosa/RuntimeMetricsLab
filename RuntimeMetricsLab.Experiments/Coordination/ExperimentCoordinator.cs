using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Contracts.Runs;
using RuntimeMetricsLab.Experiments.Abstractions;
using RuntimeMetricsLab.Experiments.Catalog;
using RuntimeMetricsLab.Experiments.Metrics;

namespace RuntimeMetricsLab.Experiments.Coordination;

/// <summary>
/// Orchestrates experiment runs: enforces one active run per experiment, validates and resolves
/// parameters against catalog bounds, tracks lifecycle/cooldown state, supports cooperative
/// cancellation, and guarantees cleanup (cancellation token disposal, active-run metric decrement)
/// regardless of how a run ends.
/// </summary>
public sealed class ExperimentCoordinator : IDisposable
{
    private readonly IExperimentCatalog _catalog;
    private readonly IReadOnlyDictionary<ExperimentId, IExperimentWorkload> _workloads;
    private readonly ExperimentMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExperimentCoordinator> _logger;
    private readonly TimeSpan _cooldown;
    private readonly ConcurrentDictionary<ExperimentId, RunState> _states = new();
    private readonly ConcurrentDictionary<Guid, ExperimentId> _runIndex = new();
    private readonly ConcurrentDictionary<Guid, ExperimentRunSnapshot> _runSnapshots = new();
    private bool _disposed;

    public ExperimentCoordinator(
        IExperimentCatalog catalog,
        IEnumerable<IExperimentWorkload> workloads,
        ExperimentMetrics metrics,
        ILogger<ExperimentCoordinator> logger,
        IOptions<ExperimentCoordinatorOptions>? options = null,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog;
        _metrics = metrics;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cooldown = options?.Value.Cooldown ?? ExperimentLimits.DefaultCooldown;

        var map = new Dictionary<ExperimentId, IExperimentWorkload>();
        foreach (var workload in workloads)
        {
            if (!map.TryAdd(workload.Id, workload))
            {
                throw new InvalidOperationException($"Duplicate workload registered for experiment '{workload.Id}'.");
            }
        }

        foreach (var entry in catalog.Entries)
        {
            if (!map.ContainsKey(entry.Id))
            {
                throw new InvalidOperationException($"No workload is registered for catalog experiment '{entry.Id}'.");
            }
        }

        _workloads = map;
    }

    public IReadOnlyList<ExperimentCatalogEntry> GetCatalog() => _catalog.Entries;

    /// <summary>Resolves which experiment a run id belongs to, for callers that only have a run id at hand.</summary>
    public bool TryGetExperimentId(Guid runId, out ExperimentId experimentId) => _runIndex.TryGetValue(runId, out experimentId);

    public bool TryGetRun(Guid runId, out ExperimentRunSnapshot snapshot) => _runSnapshots.TryGetValue(runId, out snapshot!);

    public ExperimentRunSnapshot GetStatus(ExperimentId id)
    {
        _catalog.Get(id); // throws ExperimentNotFoundException if unknown
        var state = _states.GetOrAdd(id, static _ => new RunState());
        lock (state.Gate)
        {
            return Snapshot(id, state);
        }
    }

    public ExperimentRunSnapshot Start(ExperimentId id, StartExperimentRequest request)
    {
        var entry = _catalog.Get(id);
        var state = _states.GetOrAdd(id, static _ => new RunState());

        Guid runId;
        ExperimentParameters parameters;
        CancellationTokenSource cts;

        lock (state.Gate)
        {
            var now = _timeProvider.GetUtcNow();

            if (state.Status is ExperimentRunStatus.Running or ExperimentRunStatus.Cancelling)
            {
                throw new ExperimentRunAlreadyActiveException(id);
            }

            if (state.CooldownUntil is { } cooldownUntil && now < cooldownUntil)
            {
                throw new ExperimentCoolingDownException(id, cooldownUntil);
            }

            // Resolve & validate before mutating any state, so a bad request never disturbs a
            // previously-idle experiment.
            parameters = ExperimentParameterResolver.Resolve(entry, request);

            runId = Guid.NewGuid();
            cts = new CancellationTokenSource();

            state.RunId = runId;
            state.Parameters = parameters;
            state.Status = ExperimentRunStatus.Running;
            state.StartedAt = now;
            state.CompletedAt = null;
            state.CooldownUntil = null;
            state.ErrorMessage = null;
            state.Metrics = Array.Empty<RunMetricSample>();
            state.Cts = cts;
        }

        _runIndex[runId] = id;
        _metrics.RunStarted(id);
        _logger.LogInformation("Experiment {ExperimentId} run {RunId} started.", id, runId);

        _ = RunAsync(id, state, runId, parameters, cts);

        lock (state.Gate)
        {
            var snapshot = Snapshot(id, state);
            _runSnapshots[runId] = snapshot;
            return snapshot;
        }
    }

    public ExperimentRunSnapshot Cancel(ExperimentId id)
    {
        _catalog.Get(id);
        var state = _states.GetOrAdd(id, static _ => new RunState());
        CancellationTokenSource? cts;

        lock (state.Gate)
        {
            if (state.Status != ExperimentRunStatus.Running)
            {
                throw new ExperimentNotRunningException(id);
            }

            state.Status = ExperimentRunStatus.Cancelling;
            cts = state.Cts;
        }

        cts?.Cancel();
        _logger.LogInformation("Experiment {ExperimentId} run {RunId} cancellation requested.", id, state.RunId);

        lock (state.Gate)
        {
            var snapshot = Snapshot(id, state);
            if (snapshot.RunId is { } runId)
            {
                _runSnapshots[runId] = snapshot;
            }

            return snapshot;
        }
    }

    private async Task RunAsync(ExperimentId id, RunState state, Guid runId, ExperimentParameters parameters, CancellationTokenSource cts)
    {
        var workload = _workloads[id];
        var recorder = _metrics.CreateRecorder(id);
        var context = new ExperimentRunContext(runId, id, parameters, recorder, _logger);

        using var activity = ExperimentActivitySource.Instance.StartActivity($"experiment.{id}", ActivityKind.Internal);
        activity?.SetTag("experiment.id", id.ToString());
        activity?.SetTag("experiment.run_id", runId);

        string result = "failed";
        string? errorMessage = null;

        try
        {
            await workload.ExecuteAsync(context, cts.Token).ConfigureAwait(false);
            result = "completed";
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException)
        {
            result = "cancelled";
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");
        }
        catch (Exception ex)
        {
            result = "failed";
            errorMessage = ex.Message;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Experiment {ExperimentId} run {RunId} failed.", id, runId);
        }
        finally
        {
            var completedAt = _timeProvider.GetUtcNow();

            lock (state.Gate)
            {
                // Guard against a stale continuation writing over a newer run's state (should not
                // happen since Start() blocks while Running/Cancelling, but keeps this safe).
                if (state.RunId == runId)
                {
                    state.Status = result switch
                    {
                        "completed" => ExperimentRunStatus.Completed,
                        "cancelled" => ExperimentRunStatus.Cancelled,
                        _ => ExperimentRunStatus.Failed,
                    };
                    state.CompletedAt = completedAt;
                    state.ErrorMessage = errorMessage;
                    state.Metrics =
                    [
                        new("run.duration", Math.Max(0, (completedAt - state.StartedAt.GetValueOrDefault(completedAt)).TotalSeconds), "s"),
                        .. recorder.Snapshot()
                    ];
                    state.CooldownUntil = completedAt + _cooldown;
                    state.Cts = null;

                    _runSnapshots[runId] = Snapshot(id, state);
                }
            }

            var duration = state.StartedAt is { } startedAt ? completedAt - startedAt : TimeSpan.Zero;
            _metrics.RunCompleted(id, result, duration);
            cts.Dispose();

            _logger.LogInformation("Experiment {ExperimentId} run {RunId} finished with result {Result}.", id, runId, result);
        }
    }

    private ExperimentRunSnapshot Snapshot(ExperimentId id, RunState state)
    {
        var now = _timeProvider.GetUtcNow();
        var displayStatus = state.Status;

        if (state.Status is ExperimentRunStatus.Completed or ExperimentRunStatus.Cancelled or ExperimentRunStatus.Failed
            && state.CooldownUntil is { } cooldownUntil && now < cooldownUntil)
        {
            displayStatus = ExperimentRunStatus.CoolingDown;
        }

        return new ExperimentRunSnapshot(
            id,
            state.RunId,
            displayStatus,
            state.Parameters,
            state.StartedAt,
            state.CompletedAt,
            state.CooldownUntil,
            state.ErrorMessage,
            state.Metrics);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var state in _states.Values)
        {
            lock (state.Gate)
            {
                state.Cts?.Cancel();
                state.Cts?.Dispose();
                state.Cts = null;
            }
        }
    }
}
