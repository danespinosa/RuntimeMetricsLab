using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using RuntimeMetricsLab.Contracts.Attribution;
using RuntimeMetricsLab.Experiments.Attribution;

namespace RuntimeMetricsLab.Collector.Windows;

[SupportedOSPlatform("windows")]
internal sealed class PerfViewCaptureManager : ICollectorControl, IAsyncDisposable
{
    private readonly WindowsCollectorRegistration _registration;
    private readonly string _root;
    private readonly HttpClient _target;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, CollectorStatus> _captures = new();
    private readonly ILogger _logger;
    private Task? _collection;
    private Guid? _active;
    private volatile bool _cancelled;
    private bool _blocked;

    public PerfViewCaptureManager(WindowsCollectorRegistration registration, ILogger logger)
    {
        _registration = registration;
        _logger = logger;
        _root = AttributionPaths.WindowsCaptureRoot(registration.LabId);
        WindowsStorageSecurity.CreateProtectedCaptureRoot(_root, new(registration.OwnerSid));
        _target = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3), AllowAutoRedirect = false })
        {
            BaseAddress = new(registration.TargetUrl), Timeout = TimeSpan.FromSeconds(5)
        };
        _target.DefaultRequestHeaders.Add("X-Lab-Key", registration.TargetKey);
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            WindowsStorageSecurity.RejectReparsePoints(directory);
            var path = Path.Combine(directory, "collector.json");
            if (!File.Exists(path)) continue;
            var status = JsonSerializer.Deserialize<CollectorStatus>(File.ReadAllText(path)) ??
                throw new InvalidDataException("Invalid saved Windows capture state.");
            if (directory.EndsWith(".partial", StringComparison.Ordinal))
            {
                _blocked = true;
                status = status with { Phase = "partial", Error = "An interrupted capture remains. Inspect its stock PerfView log/session before another collection; it was not automatically stopped or overwritten." };
            }
            _captures.Add(status.CaptureId, status);
        }
    }

    public async Task<CapabilityStatus> Capabilities(CancellationToken token)
    {
        try
        {
            using var tool = PerfViewTool.OpenVerified(_registration.CollectorExecutable);
            var identity = await TargetIdentity(token);
            using var target = OpenTarget(identity);
            CheckStorage();
            if (_blocked) throw new InvalidOperationException("A prior interrupted PerfView process/session needs operator inspection before another capture.");
            return new(true,
                [$"Stock Microsoft PerfView {PerfViewTool.Version}; SHA-256 {PerfViewTool.Sha256}.",
                 "PerfView owns CPU sampling, buffering, CLR rundown and merge. No custom ETW collector is used.",
                 "Raw local ETL contains system-wide kernel CPU/process/thread/image events. FocusProcess filters non-kernel providers only.",
                 $"Offline analysis selects fixed target PID {identity.ProcessId}, process start, build and run nonce.",
                 "Cancellation stops the workload immediately; PerfView finishes its bounded MaxCollectSec, then rundown/merge. No global profiler stop is sent.",
                 "A successful live capture and independent calibration are still required; tool availability is not proof of attribution."],
                null, AttributionPlatform.WindowsProcess);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or HttpRequestException or Win32Exception)
        {
            _logger.LogWarning(exception, "PerfView prerequisite is unavailable");
            return new(false, [], exception.Message, AttributionPlatform.WindowsProcess);
        }
    }

    public async Task<CollectorStatus> Start(CollectorStartRequest request, CancellationToken token)
    {
        if (request.Target != "windows-workload" || request.OperationId == Guid.Empty ||
            request.DurationSeconds is < 5 or > AttributionBudgets.MaximumCaptureSeconds)
            throw new ArgumentException("Only the fixed Windows target and finite duration are accepted.");
        await _gate.WaitAsync(token);
        try
        {
            var prior = _captures.Values.SingleOrDefault(item => item.OperationId == request.OperationId);
            if (prior is not null)
                return prior.DurationSeconds == request.DurationSeconds ? prior : throw new ArgumentException("Operation parameters changed.");
            if (_blocked || _active is not null) throw new InvalidOperationException("A PerfView capture is active or requires operator inspection.");
            using var tool = PerfViewTool.OpenVerified(_registration.CollectorExecutable);
            CheckStorage();
            var identity = await TargetIdentity(token);
            using var target = OpenTarget(identity);
            var id = Guid.NewGuid();
            var status = new CollectorStatus(id, request.OperationId, "capturing", null, null, 0, null,
                $"Microsoft PerfView {PerfViewTool.Version}", "not used", DurationSeconds: request.DurationSeconds,
                Provenance: Provenance(), TargetProcessId: identity.ProcessId, TargetProcessStartUtc: identity.ProcessStartUtc);
            Directory.CreateDirectory(Partial(id));
            Save(status, Partial(id));
            _captures.Add(id, status);
            _active = id;
            _cancelled = false;
            _collection = Task.Run(() => Collect(status, identity));
            return status;
        }
        finally { _gate.Release(); }
    }

    public async Task<CollectorStatus> Get(Guid id, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return _captures.GetValueOrDefault(id) ?? throw new ArgumentException("Unknown Windows capture."); }
        finally { _gate.Release(); }
    }
    public async Task<CollectorStatus?> Find(Guid operationId, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return _captures.Values.SingleOrDefault(item => item.OperationId == operationId); }
        finally { _gate.Release(); }
    }
    public async Task<CollectorStatus> Stop(Guid id, bool cancelled, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var status = _captures.GetValueOrDefault(id) ?? throw new ArgumentException("Unknown Windows capture.");
            if (_active == id)
            {
                _cancelled |= cancelled;
                status = status with { Phase = "finalizing" };
                _captures[id] = status;
            }
            return status;
        }
        finally { _gate.Release(); }
    }

    private async Task Collect(CollectorStatus original, TargetIdentity identity)
    {
        var directory = Partial(original.CaptureId);
        var status = original;
        var stage = "stock-tool-preflight";
        try
        {
            using var target = OpenTarget(identity);
            using var tool = PerfViewTool.OpenVerified(_registration.CollectorExecutable);
            var executable = PerfViewTool.PathFor(_registration.CollectorExecutable);
            var session = PerfViewTool.SessionName(_registration.LabId, original.CaptureId);
            var arguments = PerfViewTool.CollectArguments(directory, session, identity.ProcessId, original.DurationSeconds);
            await File.WriteAllTextAsync(Path.Combine(directory, "perfview.json"), JsonSerializer.Serialize(new
            {
                Tool = "Microsoft PerfView", PerfViewTool.Version, PerfViewTool.Sha256,
                SessionName = session, TargetProcessId = identity.ProcessId, identity.ProcessStartUtc, identity.InstanceId,
                original.OperationId, MaxCollectSeconds = original.DurationSeconds,
                PerfViewTool.BufferSizeMB, PerfViewTool.CircularMB, PerfViewTool.RundownSeconds,
                PerfViewTool.CpuSampleMilliseconds, PerfViewTool.KernelEvents, PerfViewTool.ClrEvents,
                Arguments = arguments, RawScope = "System-wide kernel events persisted locally; FocusProcess limits non-kernel providers, not kernel CPU.",
                Cancellation = "Work stops immediately; stock PerfView MaxCollectSec then rundown/merge owns profiler completion."
            }));
            var sessionLog = Path.Combine(directory, "session-list.log");
            var listExit = await PerfViewTool.Run(executable, [$"/LogFile:{sessionLog}", "ListSessions"], directory,
                "preflight-", TimeSpan.FromSeconds(20));
            var sessions = PerfViewTool.ReadLog(sessionLog);
            PerfViewTool.RequireSuccess(listExit, sessions);
            PerfViewTool.CheckSessionList(sessions, session);
            if (_cancelled) throw new OperationCanceledException("Cancelled before stock PerfView collection started.");
            using var revalidated = OpenTarget(identity);
            stage = "PerfView-collect-rundown-merge";
            var exit = await PerfViewTool.Run(executable, arguments, directory, "",
                TimeSpan.FromSeconds(original.DurationSeconds + PerfViewTool.RundownSeconds + PerfViewTool.MergeGraceSeconds));
            PerfViewTool.RequireSuccess(exit, PerfViewTool.ReadLog(Path.Combine(directory, "perfview.log")));
            using var finishedTarget = OpenTarget(identity);
            var trace = Path.Combine(directory, "trace.etl");
            if (!File.Exists(trace) || new FileInfo(trace).Length == 0)
                throw new InvalidDataException("PerfView exited without its merged ETL. Raw logs remain available.");
            status = original with
            {
                Phase = _cancelled ? "partial" : "complete", ExitCode = exit,
                Error = _cancelled ? "Cancelled workload; bounded stock PerfView trace is retained, not a normal per-call window." : null
            };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "PerfView capture {CaptureId} failed at {Stage}", original.CaptureId, stage);
            _blocked |= exception is TimeoutException;
            status = original with { Phase = "failed", Error = $"{stage}: {exception.Message}" };
            await File.WriteAllTextAsync(Path.Combine(directory, "collector-error.log"), exception.ToString());
        }
        finally
        {
            try
            {
                var trace = Path.Combine(directory, "trace.etl");
                if (!_blocked && File.Exists(trace))
                {
                    using var input = File.OpenRead(trace);
                    status = status with { Bytes = input.Length, Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(input)) };
                }
                foreach (var name in new[] { "stdout.log", "stderr.log" })
                    if (!File.Exists(Path.Combine(directory, name))) await File.WriteAllTextAsync(Path.Combine(directory, name), "");
                Save(status, directory);
                if (!_blocked) Directory.Move(directory, DirectoryFor(original.CaptureId));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(exception, "PerfView artifact finalization failed");
                _blocked = true;
                status = status with { Phase = "failed", Error = status.Error + " Artifact finalization failed: " + exception.Message };
            }
            await _gate.WaitAsync();
            try { _captures[original.CaptureId] = status; _active = null; }
            finally { _gate.Release(); }
        }
    }

    private async Task<TargetIdentity> TargetIdentity(CancellationToken token) =>
        (await _target.GetFromJsonAsync<TargetStatus>("api/attribution/status", token) ??
            throw new InvalidDataException("Fixed target identity is missing.")).Identity;

    private Process OpenTarget(TargetIdentity identity)
    {
        var process = Process.GetProcessById(identity.ProcessId);
        try
        {
            var actual = WindowsProcessIdentity.Read(identity.ProcessId);
            var expected = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_registration.CollectorExecutable)!,
                "..", "..", "..", "..", "RuntimeMetricsLab.ApiService"));
            if (identity.Platform != AttributionPlatform.WindowsProcess || identity.ProcessStartUtc is null ||
                actual.StartUtc != identity.ProcessStartUtc || identity.UserSid != _registration.OwnerSid ||
                actual.UserSid != _registration.OwnerSid || actual.Elevated ||
                !Path.GetFileNameWithoutExtension(actual.ImagePath).Equals("RuntimeMetricsLab.ApiService", StringComparison.OrdinalIgnoreCase) ||
                !actual.ImagePath.StartsWith(expected + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("PerfView target is not the exact approved non-elevated workload PID/start/image/owner.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }
    private void CheckStorage()
    {
        WindowsStorageSecurity.RejectReparsePoints(_root);
        if (new DriveInfo(_root).AvailableFreeSpace < 3L * 1024 * 1024 * 1024 ||
            Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) > 8L * 1024 * 1024 * 1024)
            throw new IOException("Insufficient private capture storage for stock PerfView buffers/files. No saved data was deleted.");
    }
    private string DirectoryFor(Guid id) => Path.Combine(_root, id.ToString("N"));
    private string Partial(Guid id) => DirectoryFor(id) + ".partial";
    private static void Save(CollectorStatus status, string directory)
    {
        var path = Path.Combine(directory, "collector.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(status));
        File.Move(path + ".tmp", path, true);
    }
    private static CaptureProvenance Provenance() => new(AttributionPlatform.WindowsProcess,
        AttributionTraceFormat.PerfViewEtw, "Microsoft PerfView", PerfViewTool.Version,
        "System-wide kernel CPU/process/thread/image events persisted in private local ETL. Offline reports select only the fixed lab target.",
        "Stock PerfView FocusProcess restricts CLR randomized AllocationSampling and lab markers to the selected PID.",
        PerfViewTool.CpuSampleMilliseconds * 1_000_000, true, false);

    public async ValueTask DisposeAsync()
    {
        _cancelled = true;
        if (_collection is not null) await _collection;
        _target.Dispose();
        _gate.Dispose();
    }
}
