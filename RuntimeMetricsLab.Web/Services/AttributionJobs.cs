using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading.Channels;
using RuntimeMetricsLab.Contracts.Attribution;
using RuntimeMetricsLab.Experiments.Attribution;

namespace RuntimeMetricsLab.Web.Services;

public sealed class AttributionJobs : BackgroundService
{
    private readonly AttributionRuntime _runtime;
    public AttributionPaths Paths => _runtime.Paths;
    public AttributionMode Mode => _runtime.Mode;
    private string Root => Paths.JobsRoot;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AttributionJob> _jobs = new();
    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(1);
    private readonly HttpClient _target;
    private readonly HttpClient _collector;
    private readonly ILogger<AttributionJobs> _logger;
    private readonly ProfilerConsentChallenges _consentChallenges = new();
    private Guid? _active;
    private CancellationTokenSource? _activeCancellation;

    public AttributionJobs(IConfiguration configuration, ILogger<AttributionJobs> logger, AttributionRuntime runtime)
    {
        _runtime = runtime;
        _logger = logger;
        _target = CreateControlClient(configuration, "Target");
        _collector = CreateControlClient(configuration, "Collector");
        Directory.CreateDirectory(Root);
        foreach (var path in Directory.EnumerateFiles(Root, "job.json", SearchOption.AllDirectories))
        {
            var job = JsonSerializer.Deserialize<AttributionJob>(File.ReadAllText(path)) ??
                throw new InvalidDataException($"Invalid durable job file: {path}");
            _jobs.Add(job.Id, job);
        }
    }

    public AttributionJob[] History() { lock (_gate) return _jobs.Values.OrderByDescending(job => job.Created).ToArray(); }
    public AttributionJob Get(Guid id) { lock (_gate) return _jobs.TryGetValue(id, out var job) ? job : throw new ArgumentException("Unknown attribution job."); }
    public AttributionReport? Report(Guid id)
    {
        Get(id);
        var path = LatestReportPath(id);
        return path is not null ? JsonSerializer.Deserialize<AttributionReport>(File.ReadAllText(path), AttributionJson.ReportOptions) : null;
    }

    public string? LatestReportPath(Guid id) => Directory.EnumerateFiles(DirectoryFor(id), "report-v*.json")
        .OrderByDescending(path => int.Parse(Path.GetFileNameWithoutExtension(path)["report-v".Length..])).FirstOrDefault();

    public CounterDiagnosticReport? DiagnosticReport(Guid id)
    {
        Get(id);
        var path = Path.Combine(DirectoryFor(id), "counter-diagnostic.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<CounterDiagnosticReport>(File.ReadAllText(path)) : null;
    }

    public AttributionJob Reanalyze(Guid id)
    {
        lock (_gate)
        {
            var job = Get(id);
            if (job.Kind != AttributionRunKind.Profile)
                throw new InvalidOperationException("Counter diagnostics contain no trace to reanalyze.");
            if (_active is not null || !job.IsTerminal)
                throw new InvalidOperationException("Wait for the active capture/analysis job before rebuilding a report.");
            if (!File.Exists(Path.Combine(DirectoryFor(id), "bundle.json")))
                throw new InvalidOperationException("This job has no finalized bundle to reanalyze.");
            var queued = Update(id, "queued-analysis");
            _active = id;
            _activeCancellation = new();
            if (!_queue.Writer.TryWrite(id)) throw new InvalidOperationException("Analysis queue is unavailable.");
            return queued;
        }
    }

    public async Task<CapabilityStatus> Capabilities(CancellationToken token = default)
    {
        try
        {
            var result = await Read<CapabilityStatus>(_collector, "capabilities", token);
            var target = await Read<TargetStatus>(_target, "api/attribution/status", token);
            return result with { Evidence = [.. result.Evidence, $"Target .NET {target.Identity.Runtime}; {target.Identity.ProcessorCount} runtime-visible CPUs; instance {target.Identity.InstanceId}."] };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "Attribution preflight unavailable");
            return new(false, [], "Private target or collector is unavailable: " + exception.Message);
        }
    }

    public AttributionJob Start(StartAttributionRequest request)
    {
        AttributionLessons.Validate(request.Settings);
        if (!Enum.IsDefined(request.Kind) ||
            (request.Kind == AttributionRunKind.CounterDiagnostic && Mode != AttributionMode.Windows))
            throw new ArgumentException("Counter-only diagnostics are an explicit native Windows operation.");
        if (request.IdempotencyKey == Guid.Empty) throw new ArgumentException("A non-empty idempotency key is required.");
        lock (_gate)
        {
            var existing = _jobs.Values.SingleOrDefault(job => job.IdempotencyKey == request.IdempotencyKey);
            if (existing is not null)
                return existing.Settings == request.Settings && existing.Kind == request.Kind ? existing :
                    throw new ArgumentException("Idempotency key was already used for different settings or collection kind.");
            if (_active is not null || _jobs.Values.Any(job => !job.IsTerminal))
                throw new InvalidOperationException("One capture/analysis job is already active.");
            if (_jobs.Count >= 1000 || new DriveInfo(Root).AvailableFreeSpace < 768L * 1024 * 1024)
                throw new IOException("Local attribution storage is full. Export/manage existing captures explicitly; no history was deleted.");
            var job = new AttributionJob(Guid.NewGuid(), request.IdempotencyKey, request.Settings,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "queued", Kind: request.Kind);
            Directory.CreateDirectory(DirectoryFor(job.Id));
            Persist(job);
            _active = job.Id;
            _activeCancellation = new();
            if (!_queue.Writer.TryWrite(job.Id))
                throw new InvalidOperationException("Attribution worker queue is unavailable.");
            return job;
        }
    }

    public AttributionJob Cancel(Guid id)
    {
        lock (_gate)
        {
            var job = Get(id);
            if (job.IsTerminal) return job;
            if (_active != id) throw new InvalidOperationException("Only the active job can be cancelled.");
            _consentChallenges.Revoke(id);
            _activeCancellation!.Cancel();
            return Update(id, "cancelling");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var interrupted in History().Where(job => !job.IsTerminal))
        {
            if (Mode == AttributionMode.Windows && interrupted.Kind == AttributionRunKind.Profile)
                await CancelProfilerApproval(interrupted.Id);
            var cleanup = await Cleanup(interrupted.Id, interrupted.CaptureId, true, interrupted.Kind == AttributionRunKind.Profile);
            Update(interrupted.Id, "failed", error: "Web host restarted; interrupted work is not automatically replayed. " + cleanup);
        }
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _activeCancellation!.Token);
                if (Mode != AttributionMode.Windows || Get(id).Kind != AttributionRunKind.Profile || Get(id).Phase == "queued-analysis")
                    cancellation.CancelAfter(TimeSpan.FromSeconds(Get(id).Settings.DurationSeconds + 120));
                if (Get(id).Phase == "queued-analysis")
                    await RebuildReport(id, cancellation.Token);
                else if (Get(id).Kind == AttributionRunKind.CounterDiagnostic)
                    await ExecuteDiagnostic(id, cancellation.Token);
                else
                    await ExecuteJob(id, cancellation.Token);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Durable job {JobId} failed during final persistence or cleanup", id);
                lock (_gate)
                    _jobs[id] = Get(id) with { Phase = "failed", Error = "Final state could not be persisted: " + exception.Message };
            }
            finally
            {
                lock (_gate)
                {
                    _active = null;
                    _activeCancellation?.Dispose();
                    _activeCancellation = null;
                }
            }
        }
    }

    private async Task ExecuteJob(Guid id, CancellationToken token)
    {
        Guid? captureId = null;
        var collectorAdmissionAttempted = false;
        var windowsApprovalStarted = false;
        using var workloadDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var phase = "failed";
        string? error = null;
        try
        {
            if (Mode == AttributionMode.Windows)
            {
                windowsApprovalStarted = true;
                using var approvalDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                approvalDeadline.CancelAfter(TimeSpan.FromMinutes(8));
                await ProfilerApprovalWaiter.Wait(
                    ct => Post<object, WindowsProfilerApproval>(_collector, $"windows/approvals/{id}/begin", new { }, ct),
                    ct => Read<WindowsProfilerApproval>(_collector, $"windows/approvals/{id}", ct),
                    async () => { await CancelProfilerApproval(id); },
                    approval => Update(id, approval.JobPhase, approval: approval),
                    approvalDeadline.Token);
                token.ThrowIfCancellationRequested();
            }
            workloadDeadline.CancelAfter(TimeSpan.FromSeconds(AttributionBudgets.JobSeconds(Get(id).Settings.DurationSeconds, Mode)));
            token = workloadDeadline.Token;
            Update(id, "preflight");
            var capabilities = await Capabilities(token);
            if (!capabilities.Available) throw new InvalidOperationException(capabilities.Error);
            Update(id, "preparing");
            var prepared = await Post<PrepareTargetRequest, TargetStatus>(_target, "api/attribution/prepare", new(id, Get(id).Settings), token);
            Update(id, "attaching");
            collectorAdmissionAttempted = true;
            var capture = await Post<CollectorStartRequest, CollectorStatus>(_collector, "captures",
                new(id, AttributionModes.TargetAlias(Mode), AttributionBudgets.CaptureSeconds(Get(id).Settings.DurationSeconds, Mode)), token);
            captureId = capture.CaptureId;
            Update(id, "capturing", captureId);
            await Post<object, TargetStatus>(_target, $"api/attribution/{id}/run", new { }, token);
            TargetStatus status;
            do
            {
                await Task.Delay(400, token);
                status = await Read<TargetStatus>(_target, "api/attribution/status", token);
                Update(id, "capturing", detailPhase: status.Phase);
                if (status.RunId != id || status.Identity.InstanceId != prepared.Identity.InstanceId)
                    throw new InvalidDataException("Target identity changed during capture.");
                if (status.Phase is "failed" or "cancelled") throw new InvalidOperationException(status.Error ?? status.Phase);
                var collectorStatus = await Read<CollectorStatus>(_collector, $"captures/{captureId}", token);
                if (collectorStatus.Phase is "partial" or "failed") throw new InvalidOperationException(collectorStatus.Error);
                if (collectorStatus.Phase == "complete" && status.Phase != "completed")
                    throw new InvalidDataException("Collector ended before the target completed its covered measurement and recovery.");
            } while (status.Phase != "completed");
            var measurement = status.Measurement ?? throw new InvalidDataException("Completed target did not return measurement evidence.");
            Update(id, "finalizing");
            await Post<CollectorStopRequest, CollectorStatus>(_collector, $"captures/{captureId}/stop", new(false), token);
            var finalizeClock = Stopwatch.StartNew();
            do
            {
                await Task.Delay(250, token);
                capture = await Read<CollectorStatus>(_collector, $"captures/{captureId}", token);
                if (finalizeClock.Elapsed > TimeSpan.FromSeconds(AttributionBudgets.FinalizationSeconds(Mode)))
                    throw new TimeoutException("Collector finalization exceeded its deadline.");
            } while (capture.Phase is "capturing" or "finalizing");
            if (capture.Phase != "complete")
                throw new InvalidDataException(capture.Error ?? "Incomplete collector artifact.");
            var targetPath = Path.Combine(DirectoryFor(id), "target.json");
            await WriteImmutable(targetPath, measurement);
            var hashes = new Dictionary<string, string> { ["target.json"] = await Hash(targetPath) };
            var format = AttributionModes.Provenance(capture).Format;
            var traceName = AttributionModes.TraceFileName(format);
            var captureDirectory = Paths.Capture(capture.CaptureId);
            foreach (var name in AttributionArtifacts.CollectorFiles(format,
                File.Exists(Path.Combine(captureDirectory, AttributionArtifacts.WindowsRundown))))
                hashes[name] = await Hash(Path.Combine(captureDirectory, name));
            var bundle = new CaptureBundle(1, id, measurement, capture, traceName, DateTimeOffset.UtcNow, hashes);
            await WriteImmutable(Path.Combine(DirectoryFor(id), "bundle.json"), bundle);
            await PostEmpty(_target, $"api/attribution/{id}/release", token);
            Update(id, "analyzing");
            await Analyze(id, token);
            var report = Report(id) ?? throw new InvalidDataException("Analysis worker did not create a report.");
            Update(id, "analyzing", revision: report.Revision);
            phase = report.Quality.Any(notice => notice.Severity == "error") ? "partial" : "ready";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            phase = "cancelled";
            error = "Cancelled or the finite job deadline expired. Incomplete artifacts do not receive normal per-call estimates.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Attribution job {JobId} failed", id);
            error = exception.Message;
        }
        finally
        {
            _consentChallenges.Revoke(id);
            if (windowsApprovalStarted && !collectorAdmissionAttempted)
            {
                var approvalCleanup = await CancelProfilerApproval(id);
                if (approvalCleanup is not null) error = string.Join(" ", error, approvalCleanup);
            }
            var cleanup = await Cleanup(id, captureId, phase is not ("ready" or "partial"), collectorAdmissionAttempted);
            if (!string.IsNullOrEmpty(cleanup))
            {
                error = (error is null ? "" : error + " ") + cleanup;
                if (phase == "ready") phase = "partial";
            }
            Update(id, phase, captureId, error);
        }
    }

    private async Task ExecuteDiagnostic(Guid id, CancellationToken token)
    {
        var phase = "failed";
        string? error = null;
        try
        {
            Update(id, "preparing");
            var prepared = await Post<PrepareTargetRequest, TargetStatus>(_target, "api/attribution/prepare",
                new(id, Get(id).Settings), token);
            await Post<object, TargetStatus>(_target, $"api/attribution/{id}/counter-diagnostic", new { }, token);
            TargetStatus status;
            do
            {
                await Task.Delay(200, token);
                status = await Read<TargetStatus>(_target, "api/attribution/status", token);
                if (status.RunId != id || status.Identity.InstanceId != prepared.Identity.InstanceId)
                    throw new InvalidDataException("The diagnostic target identity changed.");
                Update(id, "counter-diagnostic", detailPhase: status.Phase);
                if (status.Phase is "failed" or "cancelled") throw new InvalidOperationException(status.Error ?? status.Phase);
            } while (status.Phase != "completed");
            var measurement = status.Measurement ?? throw new InvalidDataException("Counter measurements are missing.");
            await WriteImmutable(Path.Combine(DirectoryFor(id), "target.json"), measurement);
            var seconds = (measurement.Window.End.MonotonicTicks - measurement.Window.Start.MonotonicTicks) /
                (double)measurement.Window.Frequency;
            var cpu = AttributionMetrics.Delta(measurement.Window, "dotnet.process.cpu.time", "cpu.mode", "user") +
                AttributionMetrics.Delta(measurement.Window, "dotnet.process.cpu.time", "cpu.mode", "system");
            var report = new CounterDiagnosticReport(1, id, measurement, seconds, cpu,
                AttributionMetrics.IntervalCores(measurement.Window),
                AttributionMetrics.Delta(measurement.Window, "dotnet.gc.heap.total_allocated"));
            await WriteImmutable(Path.Combine(DirectoryFor(id), "counter-diagnostic.json"), report);
            phase = "diagnostic-ready";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            phase = "cancelled";
            error = "Counter diagnostic cancelled; no CPU profile was collected.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Counter diagnostic {JobId} failed", id);
            error = exception.Message;
        }
        finally
        {
            var cleanup = await Cleanup(id, null, phase != "diagnostic-ready", false);
            if (cleanup is not null) { error = string.Join(" ", error, cleanup); phase = "failed"; }
            Update(id, phase, error: error);
        }
    }

    private async Task RebuildReport(Guid id, CancellationToken token)
    {
        try
        {
            Update(id, "analyzing");
            await Analyze(id, token);
            var report = Report(id) ?? throw new InvalidDataException("No rebuilt report was saved.");
            Update(id, report.Quality.Any(notice => notice.Severity == "error") ? "partial" : "ready", revision: report.Revision);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Update(id, "cancelled", error: "Report rebuilding was cancelled. Previous reports and raw data were preserved.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Report rebuilding failed for {JobId}", id);
            Update(id, "failed", error: exception.Message);
        }
    }

    private async Task<string?> Cleanup(Guid id, Guid? captureId, bool cancelled, bool includeCollector = true)
    {
        var errors = new List<string>();
        // Cancel measured work first; collector and lease cleanup get independent bounded attempts.
        using (var targetDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
        {
            try
            {
                var target = await Read<TargetStatus>(_target, "api/attribution/status", targetDeadline.Token);
                if (target.RunId == id && target.Phase is not ("completed" or "cancelled" or "failed"))
                {
                    await Post<object, TargetStatus>(_target, $"api/attribution/{id}/cancel", new { }, targetDeadline.Token);
                    while ((await Read<TargetStatus>(_target, "api/attribution/status", targetDeadline.Token)).Phase is not ("completed" or "cancelled" or "failed"))
                        await Task.Delay(100, targetDeadline.Token);
                }
                if (cancelled)
                {
                    target = await Read<TargetStatus>(_target, "api/attribution/status", targetDeadline.Token);
                    var partialPath = Path.Combine(DirectoryFor(id), "partial-target.json");
                    if (target.RunId == id && !File.Exists(partialPath))
                        await WriteImmutable(partialPath, target);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                _logger.LogError(exception, "Target cancellation failed for {JobId}", id);
                errors.Add("Target cancellation requires attention: " + exception.Message);
            }
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(AttributionBudgets.FinalizationSeconds(Mode)));
        try
        {
            if (includeCollector && captureId is null)
            {
                var found = await _collector.GetAsync($"captures/by-operation/{id}", deadline.Token);
                using (found)
                {
                    if (found.StatusCode != System.Net.HttpStatusCode.NotFound)
                        await Ensure(found, deadline.Token);
                    if (found.IsSuccessStatusCode)
                        captureId = (await found.Content.ReadFromJsonAsync<CollectorStatus>(deadline.Token))?.CaptureId;
                }
            }
            if (includeCollector && captureId is { } capture)
            {
                await Post<CollectorStopRequest, CollectorStatus>(_collector, $"captures/{capture}/stop", new(cancelled), deadline.Token);
                while ((await Read<CollectorStatus>(_collector, $"captures/{capture}", deadline.Token)).Phase is "capturing" or "finalizing")
                    await Task.Delay(200, deadline.Token);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            _logger.LogError(exception, "Collector cleanup failed for {JobId}", id);
            errors.Add("Collector cleanup requires attention: " + exception.Message);
        }
        using var releaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            var target = await Read<TargetStatus>(_target, "api/attribution/status", releaseDeadline.Token);
            if (target.RunId == id)
            {
                // Release is idempotent at the target, including cleanup after successful finalization.
                await PostEmpty(_target, $"api/attribution/{id}/release", releaseDeadline.Token);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            _logger.LogError(exception, "Target cleanup failed for {JobId}", id);
            errors.Add("Target cleanup requires attention: " + exception.Message);
        }
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    private async Task Analyze(Guid id, CancellationToken token)
    {
        var start = _runtime.AnalysisProcess(id);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Analysis worker did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            var clock = Stopwatch.StartNew();
            while (!process.HasExited)
            {
                token.ThrowIfCancellationRequested();
                process.Refresh();
                if (clock.Elapsed > TimeSpan.FromSeconds(75) || process.WorkingSet64 > 1024L * 1024 * 1024)
                    throw new InvalidDataException("Analysis exceeded its 75-second or 1-GiB process working-set safeguard. Raw capture is retained.");
                await Task.Delay(100, token);
            }
            await process.WaitForExitAsync(token);
            await File.WriteAllTextAsync(Path.Combine(DirectoryFor(id), "analysis.log"), await stdout + await stderr, token);
            if (process.ExitCode != 0)
                throw new InvalidDataException($"Analysis worker failed (exit {process.ExitCode}). " +
                    (await stderr).Split('\n')[0] + " See the saved analysis.log.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    public string CreateProfilerConsentChallenge(Guid id)
    {
        lock (_gate)
        {
            var job = Get(id);
            if (Mode != AttributionMode.Windows || job.IsTerminal || job.Phase != "awaiting-consent" ||
                job.ProfilerApproval is not { Phase: ProfilerApprovalPhase.AwaitingConsent } approval ||
                _activeCancellation?.IsCancellationRequested != false)
                throw new InvalidOperationException("This job is not awaiting a current profiler scope decision.");
            return _consentChallenges.Issue(id, approval.RegistrationGeneration, approval.DeadlineUtc);
        }
    }

    public async Task AcceptProfilerConsent(Guid id, string challenge, CancellationToken token = default)
    {
        WindowsProfilerApproval approval;
        lock (_gate)
        {
            var job = Get(id);
            if (Mode != AttributionMode.Windows || _active != id || job.IsTerminal ||
                _activeCancellation?.IsCancellationRequested != false || job.ProfilerApproval is null)
                throw new InvalidOperationException("The profiler request is no longer active.");
            approval = job.ProfilerApproval;
            _consentChallenges.Consume(id, approval.RegistrationGeneration, challenge);
        }
        var accepted = await Post<WindowsApprovalDecision, WindowsProfilerApproval>(_collector, $"windows/approvals/{id}/accept",
            new(approval.RegistrationGeneration), token);
        lock (_gate)
        {
            if (!Get(id).IsTerminal && _activeCancellation?.IsCancellationRequested == false)
                Update(id, accepted.JobPhase, approval: accepted);
        }
    }

    private async Task<string?> CancelProfilerApproval(Guid id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await _collector.PostAsJsonAsync($"windows/approvals/{id}/cancel", new { }, deadline.Token);
            await Ensure(response, deadline.Token);
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(exception, "Profiler approval cancellation could not be delivered for {JobId}", id);
            return "Profiler authorization cancellation could not be delivered; the pending-grant heartbeat/expiry still prevents stale work. " + exception.Message;
        }
    }

    private AttributionJob Update(Guid id, string phase, Guid? captureId = null, string? error = null, int? revision = null,
        string? detailPhase = null, WindowsProfilerApproval? approval = null)
    {
        lock (_gate)
        {
            var current = Get(id);
            var updated = current with { Phase = phase, Updated = DateTimeOffset.UtcNow, CaptureId = captureId ?? current.CaptureId,
                Error = error, ReportRevision = revision ?? current.ReportRevision, DetailPhase = detailPhase,
                ProfilerApproval = approval ?? current.ProfilerApproval };
            Persist(updated);
            return updated;
        }
    }
    private void Persist(AttributionJob job)
    {
        var path = Path.Combine(DirectoryFor(job.Id), "job.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(job));
        File.Move(path + ".tmp", path, true);
        _jobs[job.Id] = job;
    }
    public string DirectoryFor(Guid id) => Paths.Job(id);
    private static async Task<string> Hash(string path)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input));
    }
    private static async Task WriteImmutable<T>(string path, T value)
    {
        await using (var stream = new FileStream(path + ".tmp", FileMode.CreateNew))
            await JsonSerializer.SerializeAsync(stream, value);
        File.Move(path + ".tmp", path);
    }
    private static HttpClient CreateControlClient(IConfiguration configuration, string name)
    {
        // Deliberately independent of generic HTTP resilience: starts/stops are never automatically retried.
        var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
        {
            BaseAddress = new(configuration[$"Attribution:{name}Url"] ?? throw new InvalidOperationException($"{name} URL is required.")),
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Add("X-Lab-Key", configuration[$"Attribution:{name}Key"] ??
            throw new InvalidOperationException($"{name} credential is required."));
        return client;
    }
    private static async Task<T> Read<T>(HttpClient client, string path, CancellationToken token)
    {
        using var response = await client.GetAsync(path, token);
        await Ensure(response, token);
        return await response.Content.ReadFromJsonAsync<T>(token) ?? throw new InvalidDataException("Missing control response.");
    }
    private static async Task<TResponse> Post<TRequest, TResponse>(HttpClient client, string path, TRequest request, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(path, request, token);
        await Ensure(response, token);
        return await response.Content.ReadFromJsonAsync<TResponse>(token) ?? throw new InvalidDataException("Missing control response.");
    }
    private static async Task PostEmpty(HttpClient client, string path, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(path, new { }, token);
        await Ensure(response, token);
    }
    private static async Task Ensure(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Private control returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(token)}");
    }
    public override void Dispose()
    {
        _activeCancellation?.Cancel();
        _target.Dispose(); _collector.Dispose();
        base.Dispose();
    }
}
