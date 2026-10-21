using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using RuntimeMetricsLab.Analysis;
using RuntimeMetricsLab.Experiments.Attribution;

[SupportedOSPlatform("windows")]
internal static class WindowsAllocationProbe
{
    public static int RunTarget()
    {
        using var process = Process.GetCurrentProcess();
        if (WindowsToken.IsElevated(process.Handle)) throw new InvalidOperationException("Allocation probe target must not be elevated.");
        var identity = new WindowsTargetEnvironment().ReadIdentity();
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "identity", identity }));
        var ready = Stopwatch.StartNew();
        while (!AttributionMarkers.Log.IsEnabled())
        {
            if (ready.Elapsed > TimeSpan.FromSeconds(15)) return 2;
            Thread.Sleep(10);
        }
        var id = Guid.NewGuid().ToString("N");
        var counts = new AttributionLessons.Counts();
        var start = Stopwatch.GetTimestamp();
        var before = GC.GetAllocatedBytesForCurrentThread();
        AttributionMarkers.Log.Boundary(id, "allocation-start", start);
        for (var index = 0; index < 512; index++)
        {
            GC.KeepAlive(AttributionLessons.AllocateLarge(counts));
            Thread.Sleep(1);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var end = Stopwatch.GetTimestamp();
        AttributionMarkers.Log.Boundary(id, "allocation-end", end);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "measurement", runId = id, elapsedSeconds = Stopwatch.GetElapsedTime(start, end).TotalSeconds,
            entered = counts.Entered[3], completed = counts.Completed[3],
            methodSameThreadBytes = counts.Allocated[3], windowSameThreadBytes = allocated,
            cpuMethodAttribution = "Unavailable: no on-CPU provider was enabled."
        }));
        Thread.Sleep(3000);
        return 0;
    }

    public static async Task<int> Run(string output)
    {
        using var self = Process.GetCurrentProcess();
        if (WindowsToken.IsElevated(self.Handle)) throw new InvalidOperationException("This target-only probe must remain non-elevated.");
        var directory = Path.GetFullPath(output);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "allocation-only.nettrace");
        if (File.Exists(path)) throw new IOException("Choose a new probe output directory; existing traces are immutable.");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--allocation-target");
        using var target = Process.Start(start) ?? throw new InvalidOperationException("Target did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var measurementReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = ReadOutput();
        var stderr = target.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            var client = new DiagnosticsClient(target.Id);
            var retry = Stopwatch.StartNew();
            while (!DiagnosticsClient.GetPublishedProcesses().Contains(target.Id))
            {
                if (retry.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Child diagnostic pipe not published.");
                await Task.Delay(25, deadline.Token);
            }
            using var session = client.StartEventPipeSession(
            [
                new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, 0x8100003801D),
                new EventPipeProvider("RuntimeMetricsLab-Attribution", EventLevel.Informational, -1)
            ], true, 64);
            await using (var trace = new FileStream(path, FileMode.CreateNew))
            {
                var copy = session.EventStream.CopyToAsync(trace, deadline.Token);
                await measurementReady.Task.WaitAsync(deadline.Token);
                session.Stop();
                await copy;
            }
            await target.WaitForExitAsync(deadline.Token);
            if (target.ExitCode != 0) throw new InvalidOperationException("Allocation target failed: " + await stderr);
            await File.WriteAllTextAsync(Path.Combine(directory, "target.jsonl"), await stdout);
            var etlx = Path.Combine(directory, "allocation-only.etlx");
            TraceLog.CreateFromEventPipeDataFile(path, etlx);
            using var log = new TraceLog(etlx);
            using var events = log.Events.GetSource();
            var sampled = new List<(double Time, long Size, string[] Stack)>();
            var markers = new List<(string Phase, double Time)>();
            events.Clr.AllocationSampling += item =>
            {
                var frames = new List<string>();
                for (var stack = item.CallStack(); stack is not null; stack = stack.Caller)
                    frames.Add(stack.CodeAddress.FullMethodName);
                sampled.Add((item.TimeStampRelativeMSec, item.ObjectSize, frames.ToArray()));
            };
            events.Dynamic.All += item =>
            {
                if (item.ProviderName == "RuntimeMetricsLab-Attribution" && item.EventName == "Boundary")
                    markers.Add((Convert.ToString(item.PayloadByName("phase")) ?? "", item.TimeStampRelativeMSec));
            };
            events.Process();
            var begin = markers.Single(item => item.Phase == "allocation-start").Time;
            var end = markers.Single(item => item.Phase == "allocation-end").Time;
            var observations = sampled.Where(item => item.Time >= begin && item.Time < end).ToArray();
            var attributed = observations.Where(item => item.Stack.Any(frame => frame.Contains("AttributionLessons", StringComparison.Ordinal) &&
                frame.Contains("AllocateLarge", StringComparison.Ordinal))).ToArray();
            if (attributed.Length < 100 || log.EventsLost != 0)
                throw new InvalidDataException("The target-only allocation capture did not contain sufficient resolved evidence.");
            var estimate = attributed.Sum(item => AllocationEstimator.EstimatedBytes(item.Size));
            var measurement = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line)).Last();
            using (measurement)
            {
                var measured = measurement.RootElement.GetProperty("methodSameThreadBytes").GetInt64();
                var ratio = estimate / measured;
                if (ratio is < 0.75 or > 1.25) throw new InvalidDataException("Independent allocation calibration exceeded the guard.");
                var report = new
                {
                    platform = "native Windows", format = "EventPipe allocation-only", elevated = false,
                    targetProcessId = target.Id, allocationSamples = attributed.Length, knownCompletedCalls = 512,
                    sameThreadAllocatedBytes = measured, independentlyEstimatedBytes = estimate,
                    estimatedBytesPerCall = estimate / 512, independentRatio = ratio, parserEventsLost = log.EventsLost,
                    cpuAttribution = "Unavailable. No kernel CPU or EventPipe thread sampler was enabled."
                };
                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(Path.Combine(directory, "allocation-evidence.json"), json);
                Console.WriteLine(json);
            }
            return 0;
        }
        finally
        {
            if (!target.HasExited) { target.Kill(); await target.WaitForExitAsync(); }
        }

        async Task<string> ReadOutput()
        {
            var lines = new List<string>();
            while (await target.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                lines.Add(line);
                using var data = JsonDocument.Parse(line);
                if (data.RootElement.GetProperty("kind").GetString() == "measurement")
                    measurementReady.TrySetResult();
            }
            if (!measurementReady.Task.IsCompleted)
                measurementReady.TrySetException(new InvalidDataException("Target exited without a measurement boundary."));
            return string.Join(Environment.NewLine, lines);
        }
    }
}
