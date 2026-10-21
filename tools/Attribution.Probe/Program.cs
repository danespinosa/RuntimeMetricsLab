using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Text.Json;

Console.WriteLine(JsonSerializer.Serialize(new
{
    Kind = "identity",
    Pid = Environment.ProcessId,
    Runtime = Environment.Version.ToString(),
    Environment.ProcessorCount,
    Uid = File.ReadLines("/proc/self/status").Single(line => line.StartsWith("Uid:")),
    Namespace = File.ResolveLinkTarget("/proc/self/ns/pid", false)?.FullName,
    DiagnosticDirectory = Path.GetTempPath()
}));

var readyTimeout = Stopwatch.StartNew();
while (!ProbeMarkers.Log.IsEnabled())
{
    if (readyTimeout.Elapsed > TimeSpan.FromMinutes(3))
    {
        Console.Error.WriteLine("No lab marker provider attached within the bounded probe window.");
        Environment.ExitCode = 2;
        return;
    }
    Thread.Sleep(100);
}

// Attachment is confirmed by the lab provider, not by a delay or the runtime-only --probe.
ProbeMarkers.Log.Boundary("attached", Stopwatch.GetTimestamp(), 0, 0);
Thread.Sleep(1000);
using var process = Process.GetCurrentProcess();
var cpuStart = process.TotalProcessorTime;
var allocatedStart = GC.GetTotalAllocatedBytes(true);
var clockStart = Stopwatch.GetTimestamp();
ProbeMarkers.Log.Boundary("measure-start", clockStart, 0, 0);
long calls = 0;
long managedBytes = 0;
long checksum = 0;
while (Stopwatch.GetElapsedTime(clockStart) < TimeSpan.FromSeconds(8) && calls < 8192)
{
    var before = GC.GetAllocatedBytesForCurrentThread();
    checksum ^= NamedCpuAndAllocationStep(calls);
    managedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
    calls++;
}
var clockEnd = Stopwatch.GetTimestamp();
var cpuEnd = process.TotalProcessorTime;
var allocatedEnd = GC.GetTotalAllocatedBytes(true);
ProbeMarkers.Log.Boundary("measure-end", clockEnd, calls, managedBytes);
Console.WriteLine(JsonSerializer.Serialize(new
{
    Kind = "measurement",
    ClockStart = clockStart,
    ClockEnd = clockEnd,
    Stopwatch.Frequency,
    Seconds = Stopwatch.GetElapsedTime(clockStart, clockEnd).TotalSeconds,
    CpuSeconds = (cpuEnd - cpuStart).TotalSeconds,
    AllocatedBytes = allocatedEnd - allocatedStart,
    Calls = calls,
    SameThreadAllocatedBytes = managedBytes,
    Checksum = checksum
}));
ProbeMarkers.Log.Boundary("wait-start", Stopwatch.GetTimestamp(), 0, 0);
Thread.Sleep(3000);
ProbeMarkers.Log.Boundary("wait-end", Stopwatch.GetTimestamp(), 0, 0);
// Keep the target alive for graceful profiler rundown, with an absolute lifetime bound.
Thread.Sleep(TimeSpan.FromSeconds(45));

[MethodImpl(MethodImplOptions.NoInlining)]
static long NamedCpuAndAllocationStep(long seed)
{
    var buffer = NamedAllocate();
    var result = NamedArithmetic(seed);
    buffer[0] = (byte)result;
    GC.KeepAlive(buffer);
    return result;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static byte[] NamedAllocate() => new byte[32 * 1024];

[MethodImpl(MethodImplOptions.NoInlining)]
static long NamedArithmetic(long seed)
{
    var value = seed;
    for (var i = 0; i < 200_000; i++)
        value = unchecked((value * 6364136223846793005L + i) ^ (value >> 11));
    return value;
}

[EventSource(Name = "RuntimeMetricsLab-Attribution")]
sealed class ProbeMarkers : EventSource
{
    public static readonly ProbeMarkers Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Boundary(string phase, long monotonicTicks, long completedCalls, long allocatedBytes) =>
        WriteEvent(1, phase, monotonicTicks, completedCalls, allocatedBytes);
}
