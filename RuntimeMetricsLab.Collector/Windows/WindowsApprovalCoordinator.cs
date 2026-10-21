using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Collector.Windows;

public sealed record AuthenticatedProfiler(Guid Generation, DateTimeOffset ExpiresUtc, CapabilityStatus Capability);
public enum ElevationLaunchKind { Started, Denied, Failed }
public sealed record ElevationLaunchResult(ElevationLaunchKind Kind, int? ProcessId = null, string? Error = null);

public interface IWindowsApprovalBackend
{
    Guid RegistrationGeneration { get; }
    Task<AuthenticatedProfiler?> Probe(CancellationToken token);
    void CreateGrant(WindowsProfilerApproval approval, DateTimeOffset brokerExpiry);
    Task<ElevationLaunchResult> Launch(WindowsProfilerApproval approval);
    void Revoke(WindowsProfilerApproval approval);
}

public interface IWindowsApprovalStore
{
    WindowsProfilerApproval[] Load();
    void Save(WindowsProfilerApproval approval);
}

public interface IWindowsApprovalControl
{
    WindowsApprovalCoordinator Approvals { get; }
    Task<WindowsProfilerActivationLease> Activate(Guid operationId, WindowsProfilerActivation activation, CancellationToken token);
    Task<WindowsProfilerApproval> ReportStartupFailure(Guid operationId, WindowsBrokerStartupFailure failure, CancellationToken token);
}

public sealed class WindowsApprovalCoordinator : IAsyncDisposable
{
    public static readonly TimeSpan ConsentTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ElevationTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BrokerLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan ClientLease = TimeSpan.FromSeconds(15);
    private readonly IWindowsApprovalBackend _backend;
    private readonly IWindowsApprovalStore _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _probeSync = new();
    private Task<AuthenticatedProfiler?>? _probe;
    private readonly Dictionary<Guid, WindowsProfilerApproval> _states = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _monitor;
    private Task? _launch;
    private bool _disposed;
    private bool _authorityUncertain;
    private int _disposeStarted;
    public bool AuthorityAvailable => !_disposed && _disposeStarted == 0 && !_authorityUncertain;

    public WindowsApprovalCoordinator(IWindowsApprovalBackend backend, IWindowsApprovalStore store, ILogger logger, TimeProvider? clock = null)
    {
        _backend = backend; _store = store; _logger = logger; _clock = clock ?? TimeProvider.System;
        foreach (var state in store.Load())
        {
            var restored = state;
            if (!state.IsTerminal)
            {
                backend.Revoke(state);
                restored = state with { Phase = ProfilerApprovalPhase.RegistrationChanged,
                    Error = "Collector proxy restarted. Pending approval was invalidated; start a new run for fresh consent." };
                store.Save(restored);
            }
            _states[state.OperationId] = restored;
        }
    }

    public void StartMonitoring() => _monitor ??= Monitor();

    public async Task<WindowsProfilerApproval> Begin(Guid operationId, CancellationToken token)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Approval requires a non-empty job identity.");
        WindowsProfilerApproval requested;
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if (_states.TryGetValue(operationId, out var existing)) return existing;
            if (_states.Values.Any(state => !state.IsTerminal && state.Phase != ProfilerApprovalPhase.Ready))
                throw new InvalidOperationException("Another job is already awaiting Windows profiler approval.");
            if (_states.Count >= 1000) throw new IOException("Approval history reached its local bound.");
            var now = _clock.GetUtcNow();
            requested = new(operationId, _backend.RegistrationGeneration, Guid.NewGuid(),
                ProfilerApprovalPhase.AwaitingConsent, now, now + ConsentTimeout, ClientLeaseExpiresUtc: now + ClientLease);
            Persist(requested);
        }
        finally { _gate.Release(); }
        var ready = await ProbeOutsideLock(token);
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            var current = Expire(Find(operationId));
            if (SameAttempt(current, requested) && current.Phase == ProfilerApprovalPhase.AwaitingConsent &&
                ready is { Capability.Available: true } && ready.ExpiresUtc > _clock.GetUtcNow() &&
                _states.Values.Any(state => state.AttemptId == ready.Generation && state.Phase == ProfilerApprovalPhase.Ready &&
                    state.ClaimedByCapture && !state.IsTerminal && state.DeadlineUtc > _clock.GetUtcNow()))
            {
                current = current with { Phase = ProfilerApprovalPhase.Ready, BrokerGeneration = ready.Generation,
                    ReusedBroker = true, DeadlineUtc = ready.ExpiresUtc };
                Persist(current);
            }
            return current;
        }
        finally { _gate.Release(); }
    }

    public async Task<WindowsProfilerApproval> Accept(Guid operationId, Guid registration, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            var state = Find(operationId);
            if (_authorityUncertain) throw new InvalidOperationException("Profiler authority storage is uncertain; no new launch or capture is permitted. Inspect storage and restart the control proxy.");
            if (registration != _backend.RegistrationGeneration || registration != state.RegistrationGeneration)
                throw new InvalidOperationException("Approval belongs to an expired collector registration. Start a new run.");
            state = Expire(state);
            if (state.IsTerminal) throw new InvalidOperationException(state.Error ?? "This approval is no longer current.");
            if (state.Phase != ProfilerApprovalPhase.AwaitingConsent) return state;
            if (_launch is { IsCompleted: false })
                throw new InvalidOperationException("An earlier Windows UAC dialog is still pending. Dismiss it before accepting another launch; the app cannot close that secure dialog.");
            var accepted = state with { Phase = ProfilerApprovalPhase.AwaitingElevation, DeadlineUtc = _clock.GetUtcNow() + ElevationTimeout,
                ClientLeaseExpiresUtc = _clock.GetUtcNow() + ClientLease };
            Persist(accepted);
            try { _backend.CreateGrant(accepted, _clock.GetUtcNow() + BrokerLifetime); }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Profiler authorization could not be persisted for {OperationId}", operationId);
                Terminate(accepted, ProfilerApprovalPhase.Failed, "Authorization storage failed: " + exception.Message);
                throw;
            }
            // RunAs can wait synchronously for secure-UI consent. It never runs on
            // the HTTP/Blazor thread and its return is not interpreted as readiness.
            _launch = Task.Run(() => LaunchAndObserve(accepted));
            return accepted;
        }
        finally { _gate.Release(); }
    }

    public async Task<WindowsProfilerApproval> Get(Guid operationId, CancellationToken token)
    {
        await Tick(token);
        await _gate.WaitAsync(token);
        try
        {
            var state = Find(operationId);
            if (!state.IsTerminal && !state.ClaimedByCapture)
            {
                state = state with { ClientLeaseExpiresUtc = _clock.GetUtcNow() + ClientLease };
                Persist(state);
            }
            return state;
        }
        finally { _gate.Release(); }
    }

    public async Task<WindowsProfilerApproval?> Cancel(Guid operationId, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!_states.TryGetValue(operationId, out var state)) return null;
            if (state.IsTerminal) return state;
            return Terminate(state, ProfilerApprovalPhase.Cancelled,
                "Profiler approval cancelled. No cancelled job will resume. The secure UAC dialog may remain; a late broker must revalidate current approval and is rejected.");
        }
        finally { _gate.Release(); }
    }

    public async Task<WindowsProfilerApproval> RequireReady(Guid operationId, CancellationToken token)
    {
        WindowsProfilerApproval snapshot;
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            snapshot = Expire(Find(operationId));
            if (snapshot.Phase != ProfilerApprovalPhase.Ready) throw new InvalidOperationException("The exact job has no approved ready profiler.");
        }
        finally { _gate.Release(); }
        var broker = await ProbeOutsideLock(token);
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            var state = Expire(Find(operationId));
            if (!SameAttempt(state, snapshot) || state.Phase != ProfilerApprovalPhase.Ready ||
                broker is null || broker.Generation != state.BrokerGeneration || !broker.Capability.Available ||
                broker.ExpiresUtc <= _clock.GetUtcNow() ||
                !_states.Values.Any(owner => owner.AttemptId == broker.Generation && owner.Phase == ProfilerApprovalPhase.Ready &&
                    owner.DeadlineUtc > _clock.GetUtcNow()))
                throw new InvalidOperationException("The approved profiler stopped, expired, or changed. Start a new job for fresh approval.");
            state = state with { ClaimedByCapture = true };
            Persist(state);
            return state;
        }
        finally { _gate.Release(); }
    }

    public async Task<WindowsProfilerActivationLease> AuthorizeActivation(Guid operationId, Guid registration, Guid attempt, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_disposed || _authorityUncertain || _disposeStarted != 0)
                return new(false, Error: "Profiler authority is unavailable or uncertain.");
            if (!_states.TryGetValue(operationId, out var state))
                return new(false, Error: "Unknown profiler operation.");
            state = Expire(state);
            if (state.IsTerminal || state.Phase == ProfilerApprovalPhase.AwaitingConsent || state.ReusedBroker ||
                state.RegistrationGeneration != registration || registration != _backend.RegistrationGeneration || state.AttemptId != attempt)
                return new(false, Error: state.Error ?? "Profiler authorization is not active for this exact generation.");
            return new(true, _clock.GetUtcNow() + TimeSpan.FromSeconds(5));
        }
        finally { _gate.Release(); }
    }

    public async Task<WindowsProfilerApproval> ReportStartupFailure(Guid operationId, WindowsBrokerStartupFailure failure, CancellationToken token)
    {
        var description = WindowsBrokerStartup.Describe(failure);
        await _gate.WaitAsync(token);
        try
        {
            var state = Find(operationId);
            if (failure.Identity.RegistrationGeneration != _backend.RegistrationGeneration ||
                state.RegistrationGeneration != failure.Identity.RegistrationGeneration || state.AttemptId != failure.Identity.AttemptId)
                throw new UnauthorizedAccessException("Startup failure belongs to another collector generation.");
            state = Expire(state);
            if (state.IsTerminal) return state;
            if (state.Phase == ProfilerApprovalPhase.AwaitingConsent || state.ReusedBroker)
                throw new UnauthorizedAccessException("No matching collector launch was approved.");
            return Terminate(state, ProfilerApprovalPhase.Failed, description);
        }
        finally { _gate.Release(); }
    }

    public async Task Tick(CancellationToken token = default)
    {
        WindowsProfilerApproval[] pending;
        await _gate.WaitAsync(token);
        try
        {
            foreach (var saved in _states.Values.ToArray())
                Expire(saved);
            pending = _states.Values.Where(state => state.Phase == ProfilerApprovalPhase.WaitingProfilerReady).ToArray();
        }
        finally { _gate.Release(); }
        foreach (var snapshot in pending)
        {
            var broker = await ProbeOutsideLock(token);
            await _gate.WaitAsync(token);
            try
            {
                var state = Expire(Find(snapshot.OperationId));
                if (!AuthorityAvailable || !SameAttempt(state, snapshot) ||
                    state.RegistrationGeneration != _backend.RegistrationGeneration ||
                    state.Phase != ProfilerApprovalPhase.WaitingProfilerReady || broker is null) continue;
                if (broker.Generation != state.AttemptId)
                {
                    Terminate(state, ProfilerApprovalPhase.Failed, "A different profiler generation answered; no workload was started.");
                }
                else if (broker.Capability.Available && broker.ExpiresUtc > _clock.GetUtcNow())
                    Persist(state with { Phase = ProfilerApprovalPhase.Ready, BrokerGeneration = broker.Generation, DeadlineUtc = broker.ExpiresUtc });
            }
            finally { _gate.Release(); }
        }
    }

    private static bool SameAttempt(WindowsProfilerApproval current, WindowsProfilerApproval snapshot) =>
        current.AttemptId == snapshot.AttemptId && current.RegistrationGeneration == snapshot.RegistrationGeneration;

    private Task<AuthenticatedProfiler?> ProbeOutsideLock(CancellationToken token)
    {
        Task<AuthenticatedProfiler?> probe;
        lock (_probeSync)
        {
            if (_probe is null || _probe.IsCompleted) _probe = ProbeOnce();
            probe = _probe;
        }
        return probe.WaitAsync(token);
    }

    private async Task<AuthenticatedProfiler?> ProbeOnce()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        return await _backend.Probe(deadline.Token).WaitAsync(deadline.Token);
    }

    private async Task LaunchAndObserve(WindowsProfilerApproval accepted)
    {
        ElevationLaunchResult result;
        try { result = await _backend.Launch(accepted); }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Windows profiler launch failed for {OperationId}", accepted.OperationId);
            result = new(ElevationLaunchKind.Failed, Error: exception.Message);
        }
        await _gate.WaitAsync();
        try
        {
            var state = Expire(Find(accepted.OperationId));
            if (_disposed || state.IsTerminal || state.AttemptId != accepted.AttemptId ||
                state.RegistrationGeneration != _backend.RegistrationGeneration || _authorityUncertain)
            {
                _backend.Revoke(accepted);
                return;
            }
            state = result.Kind switch
            {
                ElevationLaunchKind.Started when result.ProcessId is > 0 => state with
                {
                    Phase = ProfilerApprovalPhase.WaitingProfilerReady, DeadlineUtc = _clock.GetUtcNow() + ReadyTimeout
                },
                ElevationLaunchKind.Denied => state with
                {
                    Phase = ProfilerApprovalPhase.Denied, Error = "Windows UAC was declined (ERROR_CANCELLED 1223). No workload or profile started."
                },
                _ => state with { Phase = ProfilerApprovalPhase.Failed, Error = "Collector launch failed: " + (result.Error ?? "No owned process was returned.") }
            };
            if (state.IsTerminal) Terminate(state, state.Phase, state.Error!);
            else Persist(state);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Windows approval completion could not be persisted for {OperationId}", accepted.OperationId);
            Terminate(accepted, ProfilerApprovalPhase.Failed, "Approval finalization failed: " + exception.Message);
        }
        finally { _gate.Release(); }
    }

    private WindowsProfilerApproval Expire(WindowsProfilerApproval state)
    {
        if (state.IsTerminal || (state.DeadlineUtc > _clock.GetUtcNow() &&
            (state.ClaimedByCapture || state.ClientLeaseExpiresUtc is null || state.ClientLeaseExpiresUtc > _clock.GetUtcNow()))) return state;
        return Terminate(state, ProfilerApprovalPhase.Expired,
            "Profiler approval/readiness timed out. No workload will resume after late acceptance; start a new run and consent again.");
    }
    private WindowsProfilerApproval Find(Guid id) => _states.GetValueOrDefault(id) ?? throw new ArgumentException("Unknown profiler approval.");
    private void Persist(WindowsProfilerApproval state)
    {
        try { _store.Save(state); _states[state.OperationId] = state; }
        catch (Exception exception)
        {
            _authorityUncertain = true;
            Terminate(state, ProfilerApprovalPhase.Failed, "Profiler approval persistence failed: " + exception.Message);
            throw new IOException("Profiler approval storage failed; new authority is blocked and the broker must stop.", exception);
        }
    }
    private WindowsProfilerApproval Terminate(WindowsProfilerApproval state, ProfilerApprovalPhase phase, string error)
    {
        var terminal = _states.GetValueOrDefault(state.OperationId) is { IsTerminal: true } previous
            ? previous : state with { Phase = phase, Error = error };
        _states[state.OperationId] = terminal; // Irreversible even if both durable writes fail.
        var failures = new List<string>();
        try { _store.Save(terminal); } catch (Exception exception) { failures.Add("state persistence: " + exception.Message); }
        if (!state.ReusedBroker)
        {
            try { _backend.Revoke(state); } catch (Exception exception) { failures.Add("grant revocation: " + exception.Message); }
        }
        if (failures.Count > 0)
        {
            _authorityUncertain = true;
            terminal = terminal with { Error = error + " Authorization IO failed; all new activation is blocked: " + string.Join("; ", failures) };
            _states[state.OperationId] = terminal;
            _logger.LogError("Profiler authority became uncertain for {OperationId}: {Errors}", state.OperationId, terminal.Error);
        }
        return terminal;
    }
    private void ThrowIfDisposed()
    {
        if (_disposed || _disposeStarted != 0) throw new ObjectDisposedException(nameof(WindowsApprovalCoordinator));
        if (_authorityUncertain) throw new InvalidOperationException("Profiler authority storage is uncertain; inspect storage and restart the control proxy.");
    }
    private async Task Monitor()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                try { await Tick(_shutdown.Token); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { _logger.LogError(exception, "Windows approval monitor failed; requests remain blocked until valid readiness is established"); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _shutdown.Cancel();
        if (_monitor is not null) await _monitor;
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            foreach (var state in _states.Values.Where(state => !state.IsTerminal).ToArray())
            {
                Terminate(state, ProfilerApprovalPhase.RegistrationChanged, "Collector control stopped; old approval is invalid.");
            }
        }
        finally { _gate.Release(); }
        // An outstanding secure UAC dialog cannot be cancelled by this CTS. Its
        // eventual process sees the revoked, generation-bound grant and exits.
        _shutdown.Dispose();
    }
}
