using System.Security.Cryptography;
using System.Text;

namespace RuntimeMetricsLab.Contracts.Attribution;

public enum ProfilerApprovalPhase
{
    AwaitingConsent, AwaitingElevation, WaitingProfilerReady, Ready,
    Denied, Cancelled, Expired, Failed, RegistrationChanged
}

public sealed record WindowsProfilerApproval(Guid OperationId, Guid RegistrationGeneration, Guid AttemptId,
    ProfilerApprovalPhase Phase, DateTimeOffset CreatedUtc, DateTimeOffset DeadlineUtc,
    Guid? BrokerGeneration = null, bool ReusedBroker = false, string? Error = null,
    DateTimeOffset? ClientLeaseExpiresUtc = null, bool ClaimedByCapture = false)
{
    public bool IsTerminal => Phase is ProfilerApprovalPhase.Denied or ProfilerApprovalPhase.Cancelled or
        ProfilerApprovalPhase.Expired or ProfilerApprovalPhase.Failed or ProfilerApprovalPhase.RegistrationChanged;
    public string JobPhase => Phase switch
    {
        ProfilerApprovalPhase.AwaitingConsent => "awaiting-consent",
        ProfilerApprovalPhase.AwaitingElevation => "awaiting-elevation",
        ProfilerApprovalPhase.WaitingProfilerReady => "waiting-profiler-ready",
        ProfilerApprovalPhase.Ready => "profiler-ready",
        _ => "failed"
    };
}

public sealed record WindowsApprovalDecision(Guid RegistrationGeneration);
public sealed record WindowsProfilerActivation(Guid RegistrationGeneration, Guid AttemptId, int ProcessId, DateTimeOffset ProcessStartUtc);
public sealed record WindowsProfilerActivationLease(bool Allowed, DateTimeOffset? ExpiresUtc = null, string? Error = null);
public enum WindowsBrokerStartupStage { Activation, PipeCreation, ProfilePrivilege, CaptureStorage, PipeListening, PerfViewTool }
public sealed record WindowsBrokerStartupFailure(WindowsProfilerActivation Identity, WindowsBrokerStartupStage Stage,
    int? NativeErrorCode, int HResult, string Message);
public sealed record WindowsProfilerGrant(Guid RegistrationGeneration, Guid AttemptId, Guid OperationId,
    DateTimeOffset ExpiresUtc, bool Revoked = false);

/// <summary>Issued only inside an interactive server circuit, never by a launchable GET endpoint.</summary>
public sealed class ProfilerConsentChallenges(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, List<Challenge>> _challenges = new();

    public string Issue(Guid job, Guid registration, DateTimeOffset expires)
    {
        if (job == Guid.Empty || registration == Guid.Empty || expires <= _clock.GetUtcNow())
            throw new InvalidOperationException("This profiler consent request is no longer current.");
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            foreach (var pair in _challenges.ToArray())
            {
                pair.Value.RemoveAll(challenge => challenge.Expires <= _clock.GetUtcNow());
                if (pair.Value.Count == 0) _challenges.Remove(pair.Key);
            }
            if (_challenges.Values.Sum(challenges => challenges.Count) >= 1000)
                throw new InvalidOperationException("Too many outstanding consent challenges.");
            if (!_challenges.TryGetValue(job, out var issued)) _challenges[job] = issued = [];
            if (issued.Count >= 32) throw new InvalidOperationException("Too many open consent views for this job; close old views and wait for their expiry.");
            issued.Add(new(registration, SHA256.HashData(Encoding.UTF8.GetBytes(value)), expires));
        }
        return value;
    }

    public bool Consume(Guid job, Guid registration, string value)
    {
        if (value.Length != 64) throw new UnauthorizedAccessException("Invalid profiler consent challenge.");
        lock (_gate)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            var challenge = _challenges.GetValueOrDefault(job)?.SingleOrDefault(challenge =>
                challenge.Registration == registration && challenge.Expires > _clock.GetUtcNow() &&
                CryptographicOperations.FixedTimeEquals(challenge.Hash, hash));
            if (challenge is null)
                throw new UnauthorizedAccessException("Profiler consent expired or changed. Refresh the consent step.");
            // A replay may acknowledge the same admission, but cannot mint another launch.
            var firstUse = !challenge.Consumed;
            challenge.Consumed = true;
            return firstUse;
        }
    }

    public void Revoke(Guid job) { lock (_gate) _challenges.Remove(job); }
    private sealed class Challenge(Guid registration, byte[] hash, DateTimeOffset expires)
    {
        public Guid Registration { get; } = registration;
        public byte[] Hash { get; } = hash;
        public DateTimeOffset Expires { get; } = expires;
        public bool Consumed { get; set; }
    }
}

public static class ProfilerApprovalWaiter
{
    public static async Task<WindowsProfilerApproval> Wait(
        Func<CancellationToken, Task<WindowsProfilerApproval>> begin,
        Func<CancellationToken, Task<WindowsProfilerApproval>> refresh,
        Func<Task> cancel,
        Action<WindowsProfilerApproval> observe,
        CancellationToken token,
        TimeSpan? pollInterval = null)
    {
        try
        {
            var state = await begin(token);
            var registration = state.RegistrationGeneration;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (state.RegistrationGeneration != registration)
                    throw new InvalidOperationException("Collector registration changed while awaiting approval; a fresh run and consent are required.");
                observe(state);
                if (state.IsTerminal) throw new InvalidOperationException(state.Error ?? $"Profiler approval ended: {state.Phase}.");
                if (state.Phase == ProfilerApprovalPhase.Ready)
                {
                    if (state.BrokerGeneration is null) throw new InvalidDataException("Ready state has no authenticated broker generation.");
                    token.ThrowIfCancellationRequested();
                    return state;
                }
                await Task.Delay(pollInterval ?? TimeSpan.FromMilliseconds(350), token);
                state = await refresh(token);
            }
        }
        catch (Exception primary)
        {
            try { await cancel(); }
            catch (Exception cleanup)
            {
                throw new AggregateException("Profiler approval failed and cancellation delivery also failed.", primary, cleanup);
            }
            throw;
        }
    }
}
