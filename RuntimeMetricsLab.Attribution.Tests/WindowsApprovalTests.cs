using Microsoft.Extensions.Logging.Abstractions;
using RuntimeMetricsLab.Collector.Windows;
using RuntimeMetricsLab.Contracts.Attribution;
using RuntimeMetricsLab.Web.Services;

namespace RuntimeMetricsLab.Attribution.Tests;

// Launcher/broker results below are controlled fixtures. No test launches RunAs,
// accepts a UAC prompt, creates a kernel logger, or claims real profiler readiness.
public sealed class WindowsApprovalTests
{
    [Fact]
    public async Task BeginAndPolling_NeverGrantOrLaunchWithoutAcceptance()
    {
        await using var fixture = new ApprovalFixture();
        var id = Guid.NewGuid();
        var state = await fixture.Coordinator.Begin(id, default);
        Assert.Equal(ProfilerApprovalPhase.AwaitingConsent, state.Phase);
        for (var i = 0; i < 5; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(state.AttemptId, (await fixture.Coordinator.Get(id, default)).AttemptId);
            Assert.Equal(state.AttemptId, (await fixture.Coordinator.Begin(id, default)).AttemptId);
        }
        Assert.Equal(0, fixture.Backend.LaunchCount);
        Assert.Empty(fixture.Backend.Grants);
    }

    [Fact]
    public async Task DuplicateAccept_AdmitsExactlyOneAsynchronousLaunch()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Coordinator.Begin(Guid.NewGuid(), default);
        var accepted = await fixture.Coordinator.Accept(state.OperationId, state.RegistrationGeneration, default);
        var duplicate = await fixture.Coordinator.Accept(state.OperationId, state.RegistrationGeneration, default);
        await fixture.Backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ProfilerApprovalPhase.AwaitingElevation, accepted.Phase);
        Assert.Equal(accepted.AttemptId, duplicate.AttemptId);
        Assert.Equal(1, fixture.Backend.LaunchCount);
        Assert.Single(fixture.Backend.Grants);
        fixture.Backend.Complete(new(ElevationLaunchKind.Denied));
        await fixture.Until(ProfilerApprovalPhase.Denied, state.OperationId);
    }

    [Theory]
    [InlineData(ElevationLaunchKind.Denied, ProfilerApprovalPhase.Denied)]
    [InlineData(ElevationLaunchKind.Failed, ProfilerApprovalPhase.Failed)]
    public async Task DenialOrLaunchFailure_RevokesGrantAndNeverBecomesReady(ElevationLaunchKind outcome, ProfilerApprovalPhase expected)
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        fixture.Backend.Complete(new(outcome, Error: "Fixture outcome"));
        var final = await fixture.Until(expected, state.OperationId);
        Assert.True(final.IsTerminal);
        Assert.True(fixture.Backend.Grants[state.AttemptId].Revoked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequireReady(state.OperationId, default));
        if (outcome == ElevationLaunchKind.Denied) Assert.Contains("1223", final.Error);
    }

    [Fact]
    public async Task ProcessStarted_IsNotAuthenticatedReadiness()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.Until(ProfilerApprovalPhase.WaitingProfilerReady, state.OperationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequireReady(state.OperationId, default));
        fixture.Backend.MakeReady(state.AttemptId);
        var ready = await fixture.Until(ProfilerApprovalPhase.Ready, state.OperationId);
        Assert.Equal(state.AttemptId, ready.BrokerGeneration);
        Assert.True((await fixture.Coordinator.RequireReady(state.OperationId, default)).ClaimedByCapture);
    }

    [Fact]
    public async Task MissingProcessHandle_DoesNotPretendLaunchSucceeded()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        fixture.Backend.Complete(new(ElevationLaunchKind.Started));
        Assert.Contains("No owned process", (await fixture.Until(ProfilerApprovalPhase.Failed, state.OperationId)).Error);
    }

    [Fact]
    public async Task CancellingBeforeConsent_ClosesTheOperationWithoutAnyLaunch()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Coordinator.Begin(Guid.NewGuid(), default);
        await fixture.Coordinator.Cancel(state.OperationId, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.Accept(state.OperationId, state.RegistrationGeneration, default));
        Assert.Equal(0, fixture.Backend.LaunchCount);
    }

    [Fact]
    public async Task LateUacAcceptanceAfterCancel_CannotResumeOrReauthorizeTheJob()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        await fixture.Coordinator.Cancel(state.OperationId, default);
        fixture.Backend.MakeReady(state.AttemptId);
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.WaitUntil(() => fixture.Backend.Revocations >= 2);
        Assert.Equal(ProfilerApprovalPhase.Cancelled, (await fixture.Coordinator.Get(state.OperationId, default)).Phase);
        Assert.Null(await fixture.Backend.Probe(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequireReady(state.OperationId, default));
    }

    [Fact]
    public async Task LateUacAcceptanceAfterTimeout_RemainsExpired()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        for (var i = 0; i < 24; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            await fixture.Coordinator.Get(state.OperationId, default);
        }
        Assert.Equal(ProfilerApprovalPhase.Expired, (await fixture.Coordinator.Get(state.OperationId, default)).Phase);
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.WaitUntil(() => fixture.Backend.Revocations >= 2);
        Assert.True(fixture.Backend.Grants[state.AttemptId].Revoked);
    }

    [Fact]
    public async Task ConsentAndClientHeartbeatAreBoundedWithoutConsumingWorkloadTime()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Coordinator.Begin(Guid.NewGuid(), default);
        for (var i = 0; i < 12; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(ProfilerApprovalPhase.AwaitingConsent, (await fixture.Coordinator.Get(state.OperationId, default)).Phase);
        }
        // More than a one-second experiment has passed, but no experiment is started.
        Assert.Equal(0, fixture.Backend.LaunchCount);
        fixture.Clock.Advance(WindowsApprovalCoordinator.ClientLease + TimeSpan.FromSeconds(1));
        Assert.Equal(ProfilerApprovalPhase.Expired, (await fixture.Coordinator.Get(state.OperationId, default)).Phase);
    }

    [Fact]
    public async Task AuthenticatedReadyBroker_IsReusedWithoutAnotherPrompt()
    {
        await using var fixture = new ApprovalFixture();
        var first = await fixture.Accept();
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.Until(ProfilerApprovalPhase.WaitingProfilerReady, first.OperationId);
        fixture.Backend.MakeReady(first.AttemptId);
        await fixture.Until(ProfilerApprovalPhase.Ready, first.OperationId);
        await fixture.Coordinator.RequireReady(first.OperationId, default);
        var second = await fixture.Coordinator.Begin(Guid.NewGuid(), default);
        Assert.True(second.ReusedBroker);
        Assert.Equal(ProfilerApprovalPhase.Ready, second.Phase);
        Assert.Equal(first.AttemptId, second.BrokerGeneration);
        await fixture.Coordinator.Cancel(second.OperationId, default);
        Assert.False(fixture.Backend.Grants[first.AttemptId].Revoked);
        Assert.Equal(1, fixture.Backend.LaunchCount);
    }

    [Fact]
    public async Task BrokerCrashOrExpiredLifetime_RequiresFreshConsent()
    {
        await using var fixture = new ApprovalFixture();
        var first = await fixture.Accept();
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.Until(ProfilerApprovalPhase.WaitingProfilerReady, first.OperationId);
        fixture.Backend.MakeReady(first.AttemptId);
        await fixture.Until(ProfilerApprovalPhase.Ready, first.OperationId);
        await fixture.Coordinator.RequireReady(first.OperationId, default);
        fixture.Backend.Ready = null;
        var next = await fixture.Coordinator.Begin(Guid.NewGuid(), default);
        Assert.Equal(ProfilerApprovalPhase.AwaitingConsent, next.Phase);
        Assert.Equal(1, fixture.Backend.LaunchCount);
    }

    [Fact]
    public async Task WrongReadyGeneration_IsRejectedWithoutWork()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.Until(ProfilerApprovalPhase.WaitingProfilerReady, state.OperationId);
        fixture.Backend.Ready = new(Guid.NewGuid(), fixture.Clock.GetUtcNow() + TimeSpan.FromMinutes(10), new(true, [], null));
        fixture.Backend.AllowUnexpectedGeneration = true;
        var failed = await fixture.Until(ProfilerApprovalPhase.Failed, state.OperationId);
        Assert.Contains("different profiler generation", failed.Error);
    }

    [Fact]
    public async Task OutstandingOldUacDialog_PreventsASecondLaunch()
    {
        await using var fixture = new ApprovalFixture();
        var first = await fixture.Accept();
        await fixture.Coordinator.Cancel(first.OperationId, default);
        var next = await fixture.Coordinator.Begin(Guid.NewGuid(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.Accept(next.OperationId, next.RegistrationGeneration, default));
        Assert.Equal(1, fixture.Backend.LaunchCount);
        fixture.Backend.Complete(new(ElevationLaunchKind.Denied));
    }

    [Fact]
    public async Task ProxyRestart_InvalidatesPersistedPendingApprovalAndOldGeneration()
    {
        var clock = new TestClock();
        var backend = new FakeBackend(clock);
        var store = new MemoryStore();
        var first = new WindowsApprovalCoordinator(backend, store, NullLogger.Instance, clock);
        var pending = await first.Begin(Guid.NewGuid(), default);
        await first.Accept(pending.OperationId, pending.RegistrationGeneration, default);
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var replacement = new FakeBackend(clock, backend.Grants);
        await using var restarted = new WindowsApprovalCoordinator(replacement, store, NullLogger.Instance, clock);
        Assert.Equal(ProfilerApprovalPhase.RegistrationChanged, (await restarted.Get(pending.OperationId, default)).Phase);
        Assert.True(backend.Grants[pending.AttemptId].Revoked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.Accept(pending.OperationId, pending.RegistrationGeneration, default));
        backend.Complete(new(ElevationLaunchKind.Started, 123));
        await first.DisposeAsync();
    }

    [Fact]
    public async Task ReadyButUnclaimedJob_LosesItsGrantWhenWebHeartbeatStops()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.Until(ProfilerApprovalPhase.WaitingProfilerReady, state.OperationId);
        fixture.Backend.MakeReady(state.AttemptId);
        await fixture.Until(ProfilerApprovalPhase.Ready, state.OperationId);
        fixture.Clock.Advance(WindowsApprovalCoordinator.ClientLease + TimeSpan.FromSeconds(1));
        Assert.Equal(ProfilerApprovalPhase.Expired, (await fixture.Coordinator.Get(state.OperationId, default)).Phase);
        Assert.True(fixture.Backend.Grants[state.AttemptId].Revoked);
    }

    [Fact]
    public async Task ReadinessTimeoutAndGrantStorageFailure_DoNotLaunchWork()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.Until(ProfilerApprovalPhase.WaitingProfilerReady, state.OperationId);
        for (var i = 0; i < 7; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            await fixture.Coordinator.Get(state.OperationId, default);
        }
        Assert.Equal(ProfilerApprovalPhase.Expired, (await fixture.Coordinator.Get(state.OperationId, default)).Phase);
        Assert.Null(await fixture.Backend.Probe(default));
    }

    [Fact]
    public async Task GrantPersistenceFailure_CannotStartRunAs()
    {
        await using var fixture = new ApprovalFixture();
        fixture.Backend.FailGrant = true;
        var state = await fixture.Coordinator.Begin(Guid.NewGuid(), default);
        await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator.Accept(state.OperationId, state.RegistrationGeneration, default));
        Assert.Equal(0, fixture.Backend.LaunchCount);
        Assert.Equal(ProfilerApprovalPhase.Failed, (await fixture.Coordinator.Get(state.OperationId, default)).Phase);
    }

    [Fact]
    public async Task BrokerStartupFailure_IsImmediatelyVisibleAndRevokesTheCurrentAttempt()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.Until(ProfilerApprovalPhase.WaitingProfilerReady, state.OperationId);
        var failure = WindowsBrokerStartup.Failure(new(state.RegistrationGeneration, state.AttemptId, 123, fixture.Clock.GetUtcNow()),
            WindowsBrokerStartupStage.PipeCreation,
            new IOException("A required privilege is not held by the client.", unchecked((int)0x80070522)));
        var result = await fixture.Coordinator.ReportStartupFailure(state.OperationId, failure, default);
        Assert.Equal(ProfilerApprovalPhase.Failed, result.Phase);
        Assert.Contains("Win32 1314", result.Error);
        Assert.Contains("PipeCreation", result.Error);
        Assert.DoesNotContain("timed out", result.Error);
        Assert.True(fixture.Backend.Grants[state.AttemptId].Revoked);
        Assert.False((await fixture.Coordinator.AuthorizeActivation(state.OperationId, state.RegistrationGeneration, state.AttemptId, default)).Allowed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequireReady(state.OperationId, default));
    }

    [Fact]
    public async Task LateOrWrongGenerationStartupFailure_CannotRewriteCancelledHistory()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        var cancelled = await fixture.Coordinator.Cancel(state.OperationId, default);
        var failure = WindowsBrokerStartup.Failure(new(state.RegistrationGeneration, state.AttemptId, 123, fixture.Clock.GetUtcNow()),
            WindowsBrokerStartupStage.PipeCreation, new IOException("Late startup error"));
        Assert.Equal(cancelled, await fixture.Coordinator.ReportStartupFailure(state.OperationId, failure, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Coordinator.ReportStartupFailure(state.OperationId,
            failure with { Identity = failure.Identity with { RegistrationGeneration = Guid.NewGuid() } }, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Coordinator.ReportStartupFailure(state.OperationId,
            failure with { Identity = failure.Identity with { AttemptId = Guid.NewGuid() } }, default));
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
    }

    [Fact]
    public async Task ApprovalGate_OrdersConsentReadyPrepareCaptureAndWork()
    {
        await using var fixture = new ApprovalFixture();
        var id = Guid.NewGuid();
        var sequence = new List<string>();
        using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var workflow = Run();
        await fixture.WaitUntil(() => fixture.Store.States.ContainsKey(id));
        Assert.Empty(sequence);
        var state = await fixture.Coordinator.Get(id, default);
        sequence.Add("scope-consent");
        await fixture.Coordinator.Accept(id, state.RegistrationGeneration, default);
        await fixture.Backend.Entered.Task;
        Assert.Equal(["scope-consent"], sequence);
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.Until(ProfilerApprovalPhase.WaitingProfilerReady, id);
        sequence.Add("broker-authenticated-ready");
        fixture.Backend.MakeReady(state.AttemptId);
        await workflow;
        Assert.Equal(["scope-consent", "broker-authenticated-ready", "prepare", "capture-start", "run"], sequence);

        async Task Run()
        {
            await ProfilerApprovalWaiter.Wait(ct => fixture.Coordinator.Begin(id, ct),
                ct => fixture.Coordinator.Get(id, ct), async () => { await fixture.Coordinator.Cancel(id, default); },
                _ => { }, cancelled.Token, TimeSpan.FromMilliseconds(5));
            cancelled.Token.ThrowIfCancellationRequested();
            sequence.Add("prepare");
            await fixture.Coordinator.RequireReady(id, cancelled.Token);
            sequence.Add("capture-start");
            sequence.Add("run");
        }
    }

    [Fact]
    public async Task CancelledWaiter_NeverInvokesPrepareEvenIfBrokerBecomesReadyLate()
    {
        await using var fixture = new ApprovalFixture();
        var id = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var prepared = false;
        var task = Run();
        await fixture.WaitUntil(() => fixture.Store.States.ContainsKey(id));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(prepared);
        Assert.Equal(ProfilerApprovalPhase.Cancelled, (await fixture.Coordinator.Get(id, default)).Phase);
        async Task Run()
        {
            await ProfilerApprovalWaiter.Wait(ct => fixture.Coordinator.Begin(id, ct),
                ct => fixture.Coordinator.Get(id, ct), async () => { await fixture.Coordinator.Cancel(id, default); },
                _ => { }, cancellation.Token, TimeSpan.FromMilliseconds(5));
            prepared = true;
        }
    }

    [Fact]
    public void ConsentChallenge_IsJobGenerationAndExpiryBoundWithIdempotentReplayOnly()
    {
        var clock = new TestClock();
        var challenges = new ProfilerConsentChallenges(clock);
        var job = Guid.NewGuid();
        var generation = Guid.NewGuid();
        var challenge = challenges.Issue(job, generation, clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        Assert.Throws<UnauthorizedAccessException>(() => challenges.Consume(Guid.NewGuid(), generation, challenge));
        Assert.Throws<UnauthorizedAccessException>(() => challenges.Consume(job, Guid.NewGuid(), challenge));
        Assert.Throws<UnauthorizedAccessException>(() => challenges.Consume(job, generation, new string('0', 64)));
        Assert.True(challenges.Consume(job, generation, challenge));
        Assert.False(challenges.Consume(job, generation, challenge));
        challenges.Revoke(job);
        Assert.Throws<UnauthorizedAccessException>(() => challenges.Consume(job, generation, challenge));
        challenge = challenges.Issue(job, generation, clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Throws<UnauthorizedAccessException>(() => challenges.Consume(job, generation, challenge));
    }

    [Fact]
    public void TwoCircuitsAndReload_DoNotInvalidateAnotherViewsConsentChallenge()
    {
        var clock = new TestClock();
        var challenges = new ProfilerConsentChallenges(clock);
        var id = Guid.NewGuid();
        var generation = Guid.NewGuid();
        var first = challenges.Issue(id, generation, clock.GetUtcNow().AddMinutes(1));
        var second = challenges.Issue(id, generation, clock.GetUtcNow().AddMinutes(1));
        var reloaded = challenges.Issue(id, generation, clock.GetUtcNow().AddMinutes(1));
        Assert.True(challenges.Consume(id, generation, first));
        Assert.True(challenges.Consume(id, generation, second));
        Assert.True(challenges.Consume(id, generation, reloaded));
        challenges.Revoke(id);
        Assert.Throws<UnauthorizedAccessException>(() => challenges.Consume(id, generation, first));
        Assert.Throws<UnauthorizedAccessException>(() => challenges.Consume(id, generation, second));
    }

    [Fact]
    public async Task CancellationRemainsTerminalWhenBothRevocationAndPersistenceFail()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        fixture.Store.FailWrites = true;
        fixture.Backend.FailRevoke = true;
        var cancelled = await fixture.Coordinator.Cancel(state.OperationId, default);
        Assert.Equal(ProfilerApprovalPhase.Cancelled, cancelled!.Phase);
        Assert.Contains("IO failed", cancelled.Error);
        Assert.False(fixture.Backend.Grants[state.AttemptId].Revoked); // Simulates still-readable stale disk authority.
        Assert.False((await fixture.Coordinator.AuthorizeActivation(state.OperationId, state.RegistrationGeneration, state.AttemptId, default)).Allowed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.Begin(Guid.NewGuid(), default));
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.WaitUntil(() => fixture.Backend.Revocations >= 2);
        Assert.Equal(ProfilerApprovalPhase.Cancelled, (await fixture.Coordinator.Get(state.OperationId, default)).Phase);
    }

    [Fact]
    public async Task ExpiryWithUnwritableAuthority_CannotAuthorizeALateBroker()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        fixture.Store.FailWrites = true;
        fixture.Backend.FailRevoke = true;
        fixture.Clock.Advance(WindowsApprovalCoordinator.ClientLease + TimeSpan.FromSeconds(1));
        await fixture.Coordinator.Tick();
        Assert.Equal(ProfilerApprovalPhase.Expired, (await fixture.Coordinator.Get(state.OperationId, default)).Phase);
        Assert.False((await fixture.Coordinator.AuthorizeActivation(state.OperationId, state.RegistrationGeneration, state.AttemptId, default)).Allowed);
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
    }

    [Fact]
    public async Task ReadyStatePersistenceFailure_BlocksAllNewActivationAndCapture()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.Until(ProfilerApprovalPhase.WaitingProfilerReady, state.OperationId);
        fixture.Store.FailWrites = true;
        fixture.Backend.MakeReady(state.AttemptId);
        await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator.Tick());
        Assert.Equal(ProfilerApprovalPhase.Failed, (await fixture.Coordinator.Get(state.OperationId, default)).Phase);
        Assert.False((await fixture.Coordinator.AuthorizeActivation(state.OperationId, state.RegistrationGeneration, state.AttemptId, default)).Allowed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RequireReady(state.OperationId, default));
    }

    [Fact]
    public async Task BrokerActivation_NeedsAcceptedCurrentMemoryAuthorityAndHeartbeat()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Coordinator.Begin(Guid.NewGuid(), default);
        Assert.False((await fixture.Coordinator.AuthorizeActivation(state.OperationId, state.RegistrationGeneration, state.AttemptId, default)).Allowed);
        await fixture.Coordinator.Accept(state.OperationId, state.RegistrationGeneration, default);
        await fixture.Backend.Entered.Task;
        Assert.True((await fixture.Coordinator.AuthorizeActivation(state.OperationId, state.RegistrationGeneration, state.AttemptId, default)).Allowed);
        Assert.False((await fixture.Coordinator.AuthorizeActivation(state.OperationId, Guid.NewGuid(), state.AttemptId, default)).Allowed);
        Assert.False((await fixture.Coordinator.AuthorizeActivation(state.OperationId, state.RegistrationGeneration, Guid.NewGuid(), default)).Allowed);
        fixture.Clock.Advance(WindowsApprovalCoordinator.ClientLease + TimeSpan.FromSeconds(1));
        Assert.False((await fixture.Coordinator.AuthorizeActivation(state.OperationId, state.RegistrationGeneration, state.AttemptId, default)).Allowed);
        fixture.Backend.Complete(new(ElevationLaunchKind.Denied));
    }

    [Fact]
    public async Task ApprovalWaiter_PreservesPrimaryFailureWhenCleanupAlsoFails()
    {
        var primary = new InvalidOperationException("Original approval expiry");
        var cleanup = new IOException("Cancellation delivery failed");
        var error = await Assert.ThrowsAsync<AggregateException>(() => ProfilerApprovalWaiter.Wait(
            _ => Task.FromException<WindowsProfilerApproval>(primary),
            _ => throw new Exception("Must not poll"),
            () => Task.FromException(cleanup), _ => { }, CancellationToken.None));
        Assert.Same(primary, error.InnerExceptions[0]);
        Assert.Same(cleanup, error.InnerExceptions[1]);
    }

    [Fact]
    public async Task ReadinessProbe_DoesNotBlockBrokerActivationOnTheStateLock()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.Until(ProfilerApprovalPhase.WaitingProfilerReady, state.OperationId);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backend.ProbeOverride = async token =>
        {
            entered.TrySetResult();
            await activated.Task.WaitAsync(token);
            return new(state.AttemptId, fixture.Clock.GetUtcNow().AddMinutes(10), new(true, [], null));
        };
        var tick = fixture.Coordinator.Tick();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var lease = await fixture.Coordinator.AuthorizeActivation(state.OperationId, state.RegistrationGeneration, state.AttemptId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(lease.Allowed);
        activated.TrySetResult();
        await tick.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProfilerApprovalPhase.Ready, (await fixture.Coordinator.Get(state.OperationId, default)).Phase);
    }

    [Fact]
    public async Task CancellationDuringSlowProbe_InvalidatesTheReturningReadyResult()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.Until(ProfilerApprovalPhase.WaitingProfilerReady, state.OperationId);
        var completion = new TaskCompletionSource<AuthenticatedProfiler?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backend.ProbeOverride = token => { entered.TrySetResult(); return completion.Task.WaitAsync(token); };
        var tick = fixture.Coordinator.Tick();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancelled = await fixture.Coordinator.Cancel(state.OperationId, default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProfilerApprovalPhase.Cancelled, cancelled!.Phase);
        completion.SetResult(new(state.AttemptId, fixture.Clock.GetUtcNow().AddMinutes(10), new(true, [], null)));
        await tick;
        Assert.Equal(ProfilerApprovalPhase.Cancelled, (await fixture.Coordinator.Get(state.OperationId, default)).Phase);
    }

    [Fact]
    public async Task RegistrationChangeDuringSlowProbe_CannotCommitOldReadiness()
    {
        await using var fixture = new ApprovalFixture();
        var state = await fixture.Accept();
        fixture.Backend.Complete(new(ElevationLaunchKind.Started, 123));
        await fixture.Until(ProfilerApprovalPhase.WaitingProfilerReady, state.OperationId);
        var completion = new TaskCompletionSource<AuthenticatedProfiler?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backend.ProbeOverride = token => completion.Task.WaitAsync(token);
        var tick = fixture.Coordinator.Tick();
        fixture.Backend.RegistrationGeneration = Guid.NewGuid();
        completion.SetResult(new(state.AttemptId, fixture.Clock.GetUtcNow().AddMinutes(10), new(true, [], null)));
        await tick;
        Assert.False((await fixture.Coordinator.AuthorizeActivation(state.OperationId, state.RegistrationGeneration, state.AttemptId, default)).Allowed);
        Assert.NotEqual(ProfilerApprovalPhase.Ready, (await fixture.Coordinator.Get(state.OperationId, default)).Phase);
    }

    [Fact]
    public void AtomicApprovalReplacement_AllowsAnOpenDeleteSharingReader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rml-approval-{Guid.NewGuid():N}.json");
        try
        {
            WindowsApprovalFiles.Write(path, new WindowsProfilerGrant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1)));
            var original = WindowsApprovalFiles.Read<WindowsProfilerGrant>(path);
            using var heldReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            WindowsApprovalFiles.Write(path, original with { Revoked = true });
            Assert.True(WindowsApprovalFiles.Read<WindowsProfilerGrant>(path).Revoked);
            Assert.Equal(original.AttemptId, WindowsApprovalFiles.Read<WindowsProfilerGrant>(path).AttemptId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(1175)]
    public void AtomicReplacement_RetriesOnlyRecognizedTransientSharing(int code)
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        WindowsApprovalFiles.ReplaceWithTransientSharingRetry(() =>
        {
            if (++attempts < 3) throw new IOException("Fixture transient sharing", unchecked((int)0x80070000) | code);
        }, delays.Add);
        Assert.Equal(3, attempts);
        Assert.Equal(2, delays.Count);
        Assert.True(delays.Sum(item => item.TotalMilliseconds) < 1000);
    }

    [Fact]
    public void AtomicReplacement_ExhaustionAndArbitraryErrorsRemainVisible()
    {
        var attempts = 0;
        Assert.Throws<IOException>(() => WindowsApprovalFiles.ReplaceWithTransientSharingRetry(() =>
        {
            attempts++;
            throw new IOException("Still shared", unchecked((int)0x80070497));
        }, _ => { }));
        Assert.Equal(6, attempts);
        attempts = 0;
        Assert.Throws<UnauthorizedAccessException>(() => WindowsApprovalFiles.ReplaceWithTransientSharingRetry(() =>
        {
            attempts++;
            throw new UnauthorizedAccessException("Not a sharing error");
        }, _ => throw new Exception("Must not retry")));
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData("localhost", "http", "localhost:5000", "http://localhost:5000", "same-origin", true)]
    [InlineData("localhost", "http", "localhost:5000", "https://evil.example", "cross-site", false)]
    [InlineData("localhost", "http", "localhost:5000", "http://localhost:5001", "same-site", false)]
    [InlineData("rebind.example", "http", "rebind.example:5000", null, null, false)]
    [InlineData("127.0.0.1", "http", "127.0.0.1:5000", null, null, true)]
    [InlineData("localhost", "http", "localhost:5000", "null", "same-origin", false)]
    public void BrowserOriginPolicy_BlocksDrivebyAndRebinding(string host, string scheme, string authority,
        string? origin, string? fetchSite, bool expected) =>
        Assert.Equal(expected, WindowsUiOriginGuard.Allows(host, scheme, authority, origin, fetchSite));

    [Fact]
    public void LateBrokerGrant_IsRejectedAfterRevocationExpiryOrRegistrationChange()
    {
        var now = DateTimeOffset.UtcNow;
        var generation = Guid.NewGuid();
        var grant = new WindowsProfilerGrant(generation, Guid.NewGuid(), Guid.NewGuid(), now.AddMinutes(1));
        var registration = new WindowsCollectorRegistration(WindowsCollectorProtocol.RegistrationSchema, "6c1746d62e", "owner", 123, now.AddSeconds(-1),
            "http://localhost", "not-a-real-secret", "not-a-real-secret", "fixture.exe", generation, Grant: grant);
        Assert.True(WindowsGrantValidation.Matches(registration, registration, grant, now));
        Assert.False(WindowsGrantValidation.Matches(registration with { SchemaVersion = 2 }, registration, grant, now));
        Assert.False(WindowsGrantValidation.Matches(registration, registration with { SchemaVersion = 2 }, grant, now));
        Assert.False(WindowsGrantValidation.Matches(registration, registration, grant with { Revoked = true }, now));
        Assert.False(WindowsGrantValidation.Matches(registration, registration, grant, now.AddMinutes(2)));
        Assert.False(WindowsGrantValidation.Matches(registration, registration with { Generation = Guid.NewGuid() }, grant, now));
        Assert.False(WindowsGrantValidation.Matches(registration, registration with { OwnerProcessStartUtc = now }, grant, now));
        Assert.NotEqual(WindowsCollectorProtocol.PipeName("6c1746d62e", generation, grant.AttemptId),
            WindowsCollectorProtocol.PipeName("6c1746d62e", Guid.NewGuid(), grant.AttemptId));
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-09T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }
    private sealed class MemoryStore : IWindowsApprovalStore
    {
        public bool FailWrites;
        public System.Collections.Concurrent.ConcurrentDictionary<Guid, WindowsProfilerApproval> States { get; } = new();
        public WindowsProfilerApproval[] Load() => States.Values.ToArray();
        public void Save(WindowsProfilerApproval approval)
        {
            if (FailWrites) throw new IOException("Fixture state write failure");
            States[approval.OperationId] = approval;
        }
    }
    private sealed class FakeBackend(TestClock clock, Dictionary<Guid, WindowsProfilerGrant>? grants = null) : IWindowsApprovalBackend
    {
        public Guid RegistrationGeneration { get; set; } = Guid.NewGuid();
        public Dictionary<Guid, WindowsProfilerGrant> Grants { get; } = grants ?? new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ElevationLaunchResult> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LaunchCount, Revocations;
        public bool FailGrant, FailRevoke, AllowUnexpectedGeneration;
        public AuthenticatedProfiler? Ready;
        public Func<CancellationToken, Task<AuthenticatedProfiler?>>? ProbeOverride;
        public Task<AuthenticatedProfiler?> Probe(CancellationToken token) => ProbeOverride?.Invoke(token) ?? Task.FromResult(
            Ready is { } ready && ready.ExpiresUtc > clock.GetUtcNow() && (AllowUnexpectedGeneration ||
                Grants.TryGetValue(ready.Generation, out var grant) && !grant.Revoked && grant.ExpiresUtc > clock.GetUtcNow())
                ? Ready : null);
        public void CreateGrant(WindowsProfilerApproval approval, DateTimeOffset expiry)
        {
            if (FailGrant) throw new IOException("Fixture grant write failure");
            Grants[approval.AttemptId] = new(approval.RegistrationGeneration, approval.AttemptId, approval.OperationId, expiry);
        }
        public Task<ElevationLaunchResult> Launch(WindowsProfilerApproval approval)
        {
            Interlocked.Increment(ref LaunchCount); Entered.TrySetResult(); return _outcome.Task;
        }
        public void Revoke(WindowsProfilerApproval approval)
        {
            Interlocked.Increment(ref Revocations);
            if (FailRevoke) throw new IOException("Fixture revocation write failure");
            if (Grants.TryGetValue(approval.AttemptId, out var grant)) Grants[approval.AttemptId] = grant with { Revoked = true };
        }
        public void Complete(ElevationLaunchResult outcome) => _outcome.TrySetResult(outcome);
        public void MakeReady(Guid generation) => Ready = new(generation, clock.GetUtcNow() + WindowsApprovalCoordinator.BrokerLifetime,
            new(true, ["Fixture-authenticated broker, not an actual profiler."], null));
    }
    private sealed class ApprovalFixture : IAsyncDisposable
    {
        public TestClock Clock { get; } = new();
        public MemoryStore Store { get; } = new();
        public FakeBackend Backend { get; }
        public WindowsApprovalCoordinator Coordinator { get; }
        public ApprovalFixture()
        {
            Backend = new(Clock);
            Coordinator = new(Backend, Store, NullLogger.Instance, Clock);
        }
        public async Task<WindowsProfilerApproval> Accept()
        {
            var state = await Coordinator.Begin(Guid.NewGuid(), default);
            await Coordinator.Accept(state.OperationId, state.RegistrationGeneration, default);
            await Backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            return state;
        }
        public async Task<WindowsProfilerApproval> Until(ProfilerApprovalPhase phase, Guid id)
        {
            WindowsProfilerApproval? result = null;
            await WaitUntil(async () => (result = await Coordinator.Get(id, default)).Phase == phase);
            return result!;
        }
        public Task WaitUntil(Func<bool> predicate) => WaitUntil(() => Task.FromResult(predicate()));
        public async Task WaitUntil(Func<Task<bool>> predicate)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!await predicate()) await Task.Delay(5, deadline.Token);
        }
        public async ValueTask DisposeAsync()
        {
            Backend.Complete(new(ElevationLaunchKind.Denied));
            await Coordinator.DisposeAsync();
        }
    }
}
