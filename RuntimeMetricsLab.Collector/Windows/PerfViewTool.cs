using System.Diagnostics;
using System.Security.Cryptography;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Collector.Windows;

public static class PerfViewTool
{
    public const string Version = "3.2.8";
    public const string Sha256 = "E6B89A6DA0FE7DA5F64302306153C6AAC7A4BE67C800426A91AE014093E4DF2D";
    public const string KernelEvents = "Process,Thread,ImageLoad,Profile";
    public const string ClrEvents = "Default,AllocationSampling";
    public const int BufferSizeMB = 256;
    public const int CircularMB = 500;
    public const int RundownSeconds = 120;
    public const int MergeGraceSeconds = 90;
    public const double CpuSampleMilliseconds = 1;

    public static string PathFor(string collectorExecutable) => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(collectorExecutable)!, "..", "..", "..", "..",
        "artifacts", "perfview", Version, "PerfView.exe"));

    public static FileStream OpenVerified(string collectorExecutable)
    {
        var path = PathFor(collectorExecutable);
        if (!File.Exists(path))
            throw new FileNotFoundException("Stock Microsoft PerfView 3.2.8 is required. Run tools\\Get-PerfView.ps1 in this worktree.", path);
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (Convert.ToHexString(SHA256.HashData(input)) != Sha256)
                throw new InvalidDataException("PerfView does not match the pinned official Microsoft release SHA-256. Nothing was launched.");
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    public static string SessionName(string labId, Guid captureId) =>
        captureId != Guid.Empty ? $"RuntimeMetricsLab.PerfView.{AttributionPaths.ValidateLabId(labId)}.{captureId:N}" :
            throw new ArgumentException("A unique capture identity is required.");

    public static string[] CollectArguments(string directory, string sessionName, int targetPid, int seconds)
    {
        if (targetPid <= 0 || seconds is < 5 or > AttributionBudgets.MaximumCaptureSeconds)
            throw new ArgumentException("PerfView requires the validated fixed target and finite duration.");
        return
        [
            $"/LogFile:{Path.Combine(directory, "perfview.log")}",
            $"/DataFile:{Path.Combine(directory, "trace.etl")}",
            $"/SessionName:{sessionName}",
            $"/FocusProcess:{targetPid}",
            $"/MaxCollectSec:{seconds}",
            $"/BufferSizeMB:{BufferSizeMB}",
            $"/CircularMB:{CircularMB}",
            $"/RundownTimeout:{RundownSeconds}",
            "/CpuSampleMSec:1",
            $"/KernelEvents:{KernelEvents}",
            $"/ClrEvents:{ClrEvents}",
            "/Providers:*RuntimeMetricsLab-Attribution",
            "/NoView", "/Zip:false", "collect"
        ];
    }

    public static void CheckSessionList(string text, string sessionName)
    {
        if (!text.Contains("Active Session Names", StringComparison.Ordinal) ||
            !text.Contains("Total Active sessions:", StringComparison.Ordinal))
            throw new InvalidDataException("PerfView did not return a complete session listing; collection was not started.");
        var sessions = text.Split('\n').Where(line => line.StartsWith("    ", StringComparison.Ordinal))
            .Select(line => line.Trim()).ToArray();
        if (sessions.Any(name => name.StartsWith("PerfView", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ETWEventTrigger", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("RuntimeMetricsLab.PerfView.", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(sessionName + "Kernel", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Another PerfView/trigger session is active. The lab will not stop or replace another profiler; finish it before collecting.");
    }

    public static async Task<int> Run(string executable, string[] arguments, string directory, string outputPrefix,
        TimeSpan timeout)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Native symbols stay local. No symbol server, source upload or interactive authentication is added.
        var symbols = Path.Combine(directory, "symbols");
        Directory.CreateDirectory(symbols);
        start.Environment["_NT_SYMBOL_PATH"] = symbols;
        start.Environment["_NT_ALT_SYMBOL_PATH"] = symbols;
        await using var stdout = new FileStream(Path.Combine(directory, outputPrefix + "stdout.log"), FileMode.CreateNew);
        await using var stderr = new FileStream(Path.Combine(directory, outputPrefix + "stderr.log"), FileMode.CreateNew);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The verified PerfView executable did not start.");
        using var copying = new CancellationTokenSource();
        var output = process.StandardOutput.BaseStream.CopyToAsync(stdout, copying.Token);
        var error = process.StandardError.BaseStream.CopyToAsync(stderr, copying.Token);
        try { await process.WaitForExitAsync().WaitAsync(timeout); }
        catch (TimeoutException exception)
        {
            copying.Cancel();
            try { await Task.WhenAll(output, error); }
            catch (OperationCanceledException) when (copying.IsCancellationRequested) { }
            throw new TimeoutException($"Owned PerfView process {process.Id} did not finish collection/rundown/merge within {timeout.TotalSeconds} seconds. " +
                "No global stop or forced process kill was attempted; inspect its saved log before another run.", exception);
        }
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }

    public static string ReadLog(string path)
    {
        if (!File.Exists(path)) throw new IOException("PerfView did not produce its required diagnostic log.");
        if (new FileInfo(path).Length > 8L * 1024 * 1024) throw new IOException("PerfView diagnostic log exceeded its bound.");
        return File.ReadAllText(path);
    }

    public static void RequireSuccess(int exitCode, string log)
    {
        if (log.Contains("EULA has not been accepted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PerfView requires you to review and accept its EULA in the stock application. The lab never passes /AcceptEULA on your behalf.");
        if (exitCode != 0)
            throw new InvalidOperationException($"Stock PerfView failed (exit {exitCode}). See the preserved perfview.log/session-list.log; no alternate profiler was used.");
    }
}
