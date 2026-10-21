using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Collector;

public sealed class CaptureManager : IDisposable
{
    public const string TargetAlias = "linux-workload";
    private const string Root = "/captures";
    private const string Tool = "/root/.nuget/packages/dotnet-trace/10.0.750501/tools/net8.0/any/dotnet-trace.dll";
    private const long MaximumTraceBytes = 512L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CollectorStatus> _captures = new();
    private readonly ILogger<CaptureManager> _logger;
    private Process? _process;
    private Guid? _active;
    private DateTimeOffset? _stopAt;
    private bool _cancelled;

    public CaptureManager(ILogger<CaptureManager> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(Root);
        foreach (var path in Directory.EnumerateFiles(Root, "collector.json", SearchOption.AllDirectories).ToArray())
        {
            var state = JsonSerializer.Deserialize<CollectorStatus>(File.ReadAllText(path)) ??
                throw new InvalidDataException($"Invalid collector manifest: {path}");
            var directory = Path.GetDirectoryName(path)!;
            if (state.Phase is "capturing" or "finalizing" || directory.EndsWith(".partial", StringComparison.Ordinal))
            {
                var tracePath = Path.Combine(directory, "trace.nettrace");
                var bytes = File.Exists(tracePath) ? new FileInfo(tracePath).Length : 0;
                string? hash = null;
                if (bytes > 0)
                {
                    using var trace = File.OpenRead(tracePath);
                    hash = Convert.ToHexString(SHA256.HashData(trace));
                }
                state = state with { Phase = "partial", Bytes = bytes, Sha256 = hash,
                    Error = "Collector restarted before atomic finalization; raw artifacts are incomplete and preserved." };
                Save(state, directory);
                var finalDirectory = Path.Combine(Root, state.CaptureId.ToString("N"));
                if (directory != finalDirectory && !Directory.Exists(finalDirectory))
                    Directory.Move(directory, finalDirectory);
            }
            _captures.Add(state.CaptureId, state);
        }
    }

    public CapabilityStatus Capabilities()
    {
        try
        {
            CollectorBoundary.Verify();
            if (!OperatingSystem.IsLinux() || !File.Exists(Tool))
                throw new InvalidOperationException("Pinned Linux collect-linux tool is unavailable.");
            if (!File.ReadAllText("/proc/1/cmdline").Split('\0').Any(argument => Path.GetFileName(argument) == "RuntimeMetricsLab.ApiService.dll"))
                throw new InvalidOperationException("PID 1 is not the approved workload API. Target-only PID namespace is required.");
            using var userEvents = File.Open("/sys/kernel/tracing/user_events_data", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var kernel = File.ReadAllText("/proc/sys/kernel/osrelease").Trim();
            if (!Version.TryParse(kernel.Split('-')[0], out var version) || version < new Version(6, 4))
                throw new InvalidOperationException("collect-linux requires kernel 6.4+.");
            var used = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            if (used > 4L * 1024 * 1024 * 1024 || new DriveInfo(Root).AvailableFreeSpace < MaximumTraceBytes)
                throw new IOException("Capture storage is full. Export/manage old lab captures explicitly; none were deleted.");
            return new(true, [$"Linux {kernel}; user_events_data is writable by collector.",
                $"Fixed target PID 1; namespace {PidNamespace()}; start time {TargetStartTime()}.",
                "dotnet-trace 10.0.750501 / OneCollect 0.1.35021; runtime IPC and full collection readiness are verified per capture.",
                "Read-only tracefs parent, only user_events_data writable. No host PID namespace or Docker socket."], null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Attribution capability is unavailable");
            return new(false, [], exception.Message);
        }
    }

    public CollectorStatus Start(CollectorStartRequest request)
    {
        if (request.Target != TargetAlias || request.OperationId == Guid.Empty ||
            request.DurationSeconds is < 5 or > AttributionBudgets.MaximumCaptureSeconds)
            throw new ArgumentException("Only linux-workload, a non-empty operation ID, and duration 5-90 seconds are accepted.");
        lock (_gate)
        {
            var prior = _captures.Values.FirstOrDefault(capture => capture.OperationId == request.OperationId);
            if (prior is not null)
                return prior.DurationSeconds == request.DurationSeconds ? prior :
                    throw new ArgumentException("Operation ID was already used with another capture duration.");
            if (_active is not null)
                throw new InvalidOperationException("The collector already owns an active capture.");
            var capability = Capabilities();
            if (!capability.Available) throw new InvalidOperationException(capability.Error);
            var id = Guid.NewGuid();
            var status = new CollectorStatus(id, request.OperationId, "capturing", null, null, 0, null,
                TargetStartTime: TargetStartTime(), PidNamespace: PidNamespace(), DurationSeconds: request.DurationSeconds);
            // Complete durable admission before claiming the collector-wide lease.
            Directory.CreateDirectory(PartialDirectory(id));
            Save(status, PartialDirectory(id));
            _captures.Add(id, status);
            _active = id;
            _stopAt = null;
            _cancelled = false;
            _ = Task.Run(() => CollectAsync(status, request.DurationSeconds));
            return status;
        }
    }

    public CollectorStatus Get(Guid id)
    {
        lock (_gate)
            return _captures.TryGetValue(id, out var capture) ? capture : throw new ArgumentException("Unknown capture.");
    }

    public CollectorStatus? FindOperation(Guid operationId)
    {
        lock (_gate) return _captures.Values.SingleOrDefault(capture => capture.OperationId == operationId);
    }

    public CollectorStatus Stop(Guid id, bool cancelled)
    {
        lock (_gate)
        {
            var status = Get(id);
            if (_active != id) return status;
            _cancelled |= cancelled;
            _stopAt ??= DateTimeOffset.UtcNow;
            SignalOwnedProcess();
            return status;
        }
    }

    private async Task CollectAsync(CollectorStatus original, int durationSeconds)
    {
        var directory = PartialDirectory(original.CaptureId);
        var tracePath = Path.Combine(directory, "trace.nettrace");
        var status = original;
        Process? ownedProcess = null;
        var started = false;
        try
        {
            var start = new ProcessStartInfo("/usr/share/dotnet/dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory
            };
            foreach (var argument in new[] { Tool, "collect-linux", "--process-id", "1", "--profile", "cpu-sampling",
                "--providers", "Microsoft-Windows-DotNETRuntime:0x8100003801D:4,RuntimeMetricsLab-Attribution:0xFFFFFFFFFFFFFFFF:4",
                "--duration", TimeSpan.FromSeconds(durationSeconds).ToString(@"dd\:hh\:mm\:ss"), "--max-memory", "256", "--output", tracePath })
                start.ArgumentList.Add(argument);
            start.Environment["DOTNET_ROLL_FORWARD"] = "Major";
            start.Environment["DOTNET_EnableDiagnostics"] = "0";
            start.Environment["TMPDIR"] = "/tmp";
            var process = ownedProcess = new Process { StartInfo = start };
            lock (_gate)
            {
                if (!process.Start()) throw new InvalidOperationException("The pinned collector could not start.");
                started = true;
                _process = process;
                if (_stopAt is not null) SignalOwnedProcess();
            }
            var stdout = CopyBoundedLog(process.StandardOutput, Path.Combine(directory, "stdout.log"));
            var stderr = CopyBoundedLog(process.StandardError, Path.Combine(directory, "stderr.log"));
            var clock = Stopwatch.StartNew();
            while (!process.HasExited)
            {
                lock (_gate)
                {
                    if (clock.Elapsed.TotalSeconds > durationSeconds + 25 || (File.Exists(tracePath) && new FileInfo(tracePath).Length > MaximumTraceBytes))
                    {
                        _cancelled = true;
                        _stopAt ??= DateTimeOffset.UtcNow;
                        SignalOwnedProcess();
                    }
                    if (_stopAt is { } stopAt && DateTimeOffset.UtcNow - stopAt > TimeSpan.FromSeconds(10))
                    {
                        process.Kill();
                        _cancelled = true;
                    }
                }
                await Task.Delay(100);
            }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            var unchanged = TargetStartTime() == original.TargetStartTime && PidNamespace() == original.PidNamespace;
            var bytes = File.Exists(tracePath) ? new FileInfo(tracePath).Length : 0;
            string? hash = null;
            if (bytes > 0)
            {
                await using var stream = File.OpenRead(tracePath);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            }
            var successful = process.ExitCode == 0 && bytes > 0 && unchanged && !_cancelled;
            status = original with
            {
                Phase = successful ? "complete" : "partial", ExitCode = process.ExitCode, Bytes = bytes, Sha256 = hash,
                Error = successful ? null : $"Capture is incomplete (exit {process.ExitCode}, target identity unchanged={unchanged}, cancelled={_cancelled}). See local collector logs."
            };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Capture {CaptureId} failed", original.CaptureId);
            status = original with { Phase = "failed", Error = exception.Message };
        }
        finally
        {
            try
            {
                if (started && ownedProcess is { HasExited: false })
                {
                    ownedProcess.Kill();
                    await ownedProcess.WaitForExitAsync();
                }
                Save(status, directory);
                Directory.Move(directory, Path.Combine(Root, original.CaptureId.ToString("N")));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _logger.LogError(exception, "Capture {CaptureId} finalization failed; partial directory retained", original.CaptureId);
                status = status with { Phase = "failed", Error = "Finalization failed; partial artifacts retained: " + exception.Message };
                try { if (Directory.Exists(directory)) Save(status, directory); }
                catch (Exception persistenceException) when (persistenceException is IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(persistenceException, "Failed capture state could not be persisted for {CaptureId}", original.CaptureId);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _process = null;
                    _captures[original.CaptureId] = status;
                    _active = null;
                }
                ownedProcess?.Dispose();
            }
        }
    }

    private static async Task CopyBoundedLog(StreamReader reader, string path)
    {
        await using var writer = new StreamWriter(path, false, Encoding.UTF8);
        var buffer = new char[4096];
        var written = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (written + read > 256 * 1024)
                throw new IOException("Collector diagnostics exceeded the 256 KiB log limit.");
            await writer.WriteAsync(buffer.AsMemory(0, read));
            written += read;
        }
    }

    private void SignalOwnedProcess()
    {
        if (_process is { HasExited: false } process && kill(process.Id, 2) != 0)
            throw new IOException($"Cannot interrupt owned tracer: errno {Marshal.GetLastPInvokeError()}.");
    }

    private static string PartialDirectory(Guid id) => Path.Combine(Root, id.ToString("N") + ".partial");
    private static void Save(CollectorStatus status, string directory)
    {
        var path = Path.Combine(directory, "collector.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(status));
        File.Move(path + ".tmp", path, true);
    }
    private static string TargetStartTime()
    {
        var stat = File.ReadAllText("/proc/1/stat");
        return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19];
    }
    private static string PidNamespace() => new FileInfo("/proc/1/ns/pid").LinkTarget ??
        throw new IOException("Cannot resolve target PID namespace.");
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
    public void Dispose() { lock (_gate) { _cancelled = true; _stopAt = DateTimeOffset.UtcNow; SignalOwnedProcess(); } }
}
