using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using RuntimeMetricsLab.Contracts.Attribution;
using RuntimeMetricsLab.Experiments.Attribution;

namespace RuntimeMetricsLab.Collector.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsCollectorProxy : ICollectorControl, IWindowsApprovalBackend, IWindowsApprovalControl, IAsyncDisposable
{
    private readonly WindowsCollectorRegistration _registration;
    private readonly string _registrationPath;
    private readonly ILogger<WindowsCollectorProxy> _logger;
    private readonly IWindowsElevationLauncher _launcher;
    private readonly SemaphoreSlim _pipeGate = new(1, 1);
    private readonly object _grantGate = new();
    private WindowsProfilerGrant? _grant;
    private int? _expectedProcessId;
    public Guid RegistrationGeneration => _registration.Generation;
    public WindowsApprovalCoordinator Approvals { get; }

    public WindowsCollectorProxy(IConfiguration configuration, ILogger<WindowsCollectorProxy> logger,
        IWindowsElevationLauncher launcher)
    {
        _logger = logger; _launcher = launcher;
        using var process = Process.GetCurrentProcess();
        if (WindowsToken.IsElevated(process.Handle))
            throw new InvalidOperationException("The collector control proxy must remain non-elevated. Elevate only the consented broker.");
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Windows owner identity is unavailable.");
        var labId = AttributionPaths.ValidateLabId(configuration["Attribution:LabId"] ??
            throw new InvalidOperationException("Windows lab identity is required."));
        var dataRoot = AttributionPaths.WindowsDataRoot(labId);
        WindowsStorageSecurity.ProtectUserDirectory(dataRoot, owner);
        _registrationPath = Path.Combine(dataRoot, "collector-registration.json");
        var targetUrl = configuration["Attribution:TargetUrl"] ?? throw new InvalidOperationException("Fixed target URL is required.");
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var target) || !target.IsLoopback || target.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("The fixed collector target must be a local HTTP endpoint.");
        _registration = new(WindowsCollectorProtocol.RegistrationSchema, labId, owner.Value, process.Id, process.StartTime.ToUniversalTime(), targetUrl,
            RequiredKey(configuration, "Target"), RequiredKey(configuration, "Collector"),
            Path.Combine(AppContext.BaseDirectory, "RuntimeMetricsLab.Collector.exe"), Guid.NewGuid(),
            configuration["Attribution:ControlUrl"]);
        WriteJson(_registrationPath, _registration);
        Approvals = new(this, new WindowsApprovalStore(Path.Combine(dataRoot, "profiler-approvals")), logger);
        Approvals.StartMonitoring();
    }

    public async Task<CapabilityStatus> Capabilities(CancellationToken token)
    {
        if (!Approvals.AuthorityAvailable)
            return new(false, [], "Profiler authorization storage is uncertain or stopped. No new launch/capture is allowed; inspect storage and restart the collector control.",
                AttributionPlatform.WindowsProcess, true, true);
        var broker = await Probe(token);
        if (broker is not null) return broker.Capability;
        return new(false,
            ["Native workload, web UI, analysis and control proxy remain non-elevated.",
             $"The collector invokes the actual signed Microsoft PerfView {PerfViewTool.Version} executable. TraceEvent is used only for offline analysis.",
             "PerfView persists system-wide kernel CPU/process/thread/image data in a private local ETL. FocusProcess filters non-kernel providers only; offline reports select the lab target.",
             "Previous target-only trace consent does not authorize this broader raw artifact. Fresh PerfView scope consent and normal Windows UAC are required."],
            "PerfView approval is required. Run and analyze will wait for your explicit raw-trace scope consent and Windows UAC; no workload starts while waiting.",
            AttributionPlatform.WindowsProcess, true, true,
            "Choose Run and analyze and review the PerfView raw-trace scope. Only the fixed launcher and stock PerfView run elevated. The app cannot accept UAC or PerfView's EULA for you.");
    }

    public async Task<AuthenticatedProfiler?> Probe(CancellationToken token)
    {
        WindowsProfilerGrant? grant;
        lock (_grantGate) grant = _grant;
        if (grant is null || !GrantValid(grant)) return null;
        try
        {
            var reply = await Call(WindowsCollectorOperation.Capabilities, grant, token);
            var capability = reply.Capability ?? throw new InvalidDataException("Broker omitted capability information.");
            return new(grant.AttemptId, grant.ExpiresUtc, capability);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException ||
            exception is OperationCanceledException && !token.IsCancellationRequested)
        {
            _logger.LogDebug("Windows profiler is not ready: {Reason}", exception.Message);
            return null;
        }
    }

    public void CreateGrant(WindowsProfilerApproval approval, DateTimeOffset brokerExpiry)
    {
        if (approval.RegistrationGeneration != _registration.Generation)
            throw new InvalidOperationException("Collector registration changed.");
        using var verifiedTool = PerfViewTool.OpenVerified(_registration.CollectorExecutable);
        WindowsProfilerGrant? previous;
        lock (_grantGate) previous = _grant;
        if (previous is { Revoked: false } && previous.AttemptId != approval.AttemptId)
            Revoke(new(previous.OperationId, previous.RegistrationGeneration, previous.AttemptId,
                ProfilerApprovalPhase.Cancelled, DateTimeOffset.UtcNow, previous.ExpiresUtc));
        var grant = new WindowsProfilerGrant(_registration.Generation, approval.AttemptId, approval.OperationId, brokerExpiry);
        var launch = _registration with { Grant = grant };
        var launchPath = WindowsCollectorProtocol.LaunchFile(_registration.LabId, approval.AttemptId);
        var grantPath = WindowsCollectorProtocol.GrantFile(_registration.LabId, approval.AttemptId);
        WindowsStorageSecurity.RejectReparsePoints(launchPath);
        WindowsStorageSecurity.RejectReparsePoints(grantPath);
        WriteJson(grantPath, grant);
        WindowsApprovalFiles.CreateNew(launchPath, output => JsonSerializer.Serialize(output, launch));
        lock (_grantGate) { _grant = grant; _expectedProcessId = null; }
    }

    public async Task<ElevationLaunchResult> Launch(WindowsProfilerApproval approval)
    {
        WindowsProfilerGrant? grant;
        lock (_grantGate) grant = _grant;
        if (grant is null || grant.AttemptId != approval.AttemptId || !GrantValid(grant))
            return new(ElevationLaunchKind.Failed, Error: "Profiler authorization was revoked before launch.");
        var result = await _launcher.Launch(_registration with { Grant = grant },
            WindowsCollectorProtocol.LaunchFile(_registration.LabId, approval.AttemptId));
        lock (_grantGate)
            if (_grant?.AttemptId == approval.AttemptId) _expectedProcessId = result.ProcessId;
        return result;
    }

    public void Revoke(WindowsProfilerApproval approval)
    {
        var path = WindowsCollectorProtocol.GrantFile(_registration.LabId, approval.AttemptId);
        if (!File.Exists(path)) return;
        WindowsStorageSecurity.RejectReparsePoints(path);
        var grant = WindowsApprovalFiles.Read<WindowsProfilerGrant>(path);
        if (grant.AttemptId != approval.AttemptId || grant.RegistrationGeneration != approval.RegistrationGeneration ||
            grant.OperationId != approval.OperationId)
            throw new InvalidDataException("Profiler grant identity changed; refusing to revoke another authorization.");
        WriteJson(path, grant with { Revoked = true });
        lock (_grantGate)
            if (_grant?.AttemptId == grant.AttemptId) _grant = grant with { Revoked = true };
        // No Process.Kill: a late elevated process validates this exact revoked
        // grant before initialization and continuously monitors it while idle.
    }

    public async Task<WindowsProfilerActivationLease> Activate(Guid operationId, WindowsProfilerActivation activation, CancellationToken token)
    {
        if (activation.RegistrationGeneration != _registration.Generation || activation.AttemptId == Guid.Empty)
            return new(false, Error: "Stale collector registration.");
        ValidateBrokerProcess(activation);
        return await Approvals.AuthorizeActivation(operationId, activation.RegistrationGeneration, activation.AttemptId, token);
    }

    public async Task<WindowsProfilerApproval> ReportStartupFailure(Guid operationId, WindowsBrokerStartupFailure failure, CancellationToken token)
    {
        if (failure.Identity.RegistrationGeneration != _registration.Generation || failure.Identity.AttemptId == Guid.Empty)
            throw new UnauthorizedAccessException("Startup failure belongs to a stale collector registration.");
        ValidateBrokerProcess(failure.Identity);
        return await Approvals.ReportStartupFailure(operationId, failure, token);
    }

    private void ValidateBrokerProcess(WindowsProfilerActivation activation)
    {
        var actual = WindowsProcessIdentity.Read(activation.ProcessId);
        int? expected;
        lock (_grantGate) expected = _expectedProcessId;
        if (!actual.Elevated || actual.UserSid != _registration.OwnerSid || actual.StartUtc != activation.ProcessStartUtc ||
            actual.StartUtc < _registration.OwnerProcessStartUtc ||
            !actual.ImagePath.Equals(_registration.CollectorExecutable, StringComparison.OrdinalIgnoreCase) ||
            (expected.HasValue && expected != actual.ProcessId))
            throw new UnauthorizedAccessException("Untrusted profiler activation process.");
    }

    public async Task<CollectorStatus> Start(CollectorStartRequest request, CancellationToken token)
    {
        if (request.Target != "windows-workload" || request.OperationId == Guid.Empty ||
            request.DurationSeconds is < 5 or > AttributionBudgets.MaximumCaptureSeconds)
            throw new ArgumentException("Only the fixed Windows target and bounded duration are accepted.");
        await Approvals.RequireReady(request.OperationId, token);
        var reply = await Call(WindowsCollectorOperation.Start, CurrentGrant(), token, start: request);
        return reply.Capture ?? throw new InvalidDataException("Windows broker did not start a capture.");
    }
    public async Task<CollectorStatus> Get(Guid id, CancellationToken token) =>
        (await Call(WindowsCollectorOperation.Get, CurrentGrant(), token, id)).Capture ??
            throw new ArgumentException("Unknown Windows capture.");
    public async Task<CollectorStatus?> Find(Guid operationId, CancellationToken token)
    {
        WindowsProfilerGrant? grant;
        lock (_grantGate) grant = _grant;
        if (grant is null || !GrantValid(grant)) return null;
        return (await Call(WindowsCollectorOperation.Find, grant, token, operationId)).Capture;
    }
    public async Task<CollectorStatus> Stop(Guid id, bool cancelled, CancellationToken token) =>
        (await Call(WindowsCollectorOperation.Stop, CurrentGrant(), token, id, cancelled: cancelled)).Capture ??
            throw new ArgumentException("Unknown Windows capture.");

    private WindowsProfilerGrant CurrentGrant()
    {
        lock (_grantGate)
            return _grant is { } grant && GrantValid(grant) ? grant :
                throw new InvalidOperationException("No current approved broker grant.");
    }
    private bool GrantValid(WindowsProfilerGrant grant)
    {
        var path = WindowsCollectorProtocol.GrantFile(_registration.LabId, grant.AttemptId);
        if (grant.Revoked || grant.ExpiresUtc <= DateTimeOffset.UtcNow || grant.RegistrationGeneration != _registration.Generation ||
            !File.Exists(path)) return false;
        var current = WindowsApprovalFiles.Read<WindowsProfilerGrant>(path);
        return current == grant;
    }
    private async Task<WindowsCollectorReply> Call(WindowsCollectorOperation operation, WindowsProfilerGrant grant,
        CancellationToken token, Guid id = default, CollectorStartRequest? start = null, bool cancelled = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await _pipeGate.WaitAsync(deadline.Token);
        try
        {
            if (!GrantValid(grant)) throw new InvalidOperationException("Profiler grant was revoked, expired or changed.");
            await using var pipe = new NamedPipeClientStream(".",
                WindowsCollectorProtocol.PipeName(_registration.LabId, _registration.Generation, grant.AttemptId),
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token);
            var serverId = WindowsBrokerPipeFactory.ServerProcessId(pipe.SafePipeHandle);
            int? expected;
            lock (_grantGate) expected = _expectedProcessId;
            var server = WindowsProcessIdentity.Read(serverId);
            if (!server.Elevated || server.UserSid != _registration.OwnerSid ||
                !string.Equals(server.ImagePath, _registration.CollectorExecutable, StringComparison.OrdinalIgnoreCase) ||
                server.StartUtc < _registration.OwnerProcessStartUtc || (expected.HasValue && expected != server.ProcessId))
                throw new UnauthorizedAccessException("Untrusted collector pipe server; no credential was transmitted.");
            await WindowsCollectorProtocol.WriteAsync(pipe, new WindowsCollectorRequest(_registration.ControlKey, operation,
                id, start, cancelled, _registration.Generation, grant.AttemptId), deadline.Token);
            var reply = await WindowsCollectorProtocol.ReadAsync<WindowsCollectorReply>(pipe, deadline.Token);
            if (reply.RegistrationGeneration != _registration.Generation || reply.BrokerGeneration != grant.AttemptId)
                throw new UnauthorizedAccessException("The broker answered with a stale or different generation.");
            if (reply.Error is not null) throw new InvalidOperationException(reply.Error);
            return reply;
        }
        finally { _pipeGate.Release(); }
    }
    private static string RequiredKey(IConfiguration configuration, string name) =>
        configuration[$"Attribution:{name}Key"] is { Length: >= 32 } key ? key :
            throw new InvalidOperationException($"Private {name} credential is required.");
    private static void WriteJson<T>(string path, T value)
    {
        WindowsApprovalFiles.Write(path, value);
    }
    public ValueTask DisposeAsync() => Approvals.DisposeAsync();
}
