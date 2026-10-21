using System.Runtime.CompilerServices;
using RuntimeMetricsLab.Contracts.Attribution;
using RuntimeMetricsLab.Experiments.Catalog;

namespace RuntimeMetricsLab.Experiments.Attribution;

public static class AttributionLessons
{
    public static readonly string[] MethodNames = ["Arithmetic", "Checksum", "AllocateSmall", "AllocateLarge", "RetainPayload", "WaitStep", "MixedPipeline"];

    public static void Validate(LessonSettings settings)
    {
        if (settings.Lesson is not ("cpu" or "allocation" or "retention" or "mixed"))
            throw new ArgumentException("Select cpu, allocation, retention, or mixed.");
        if (settings.Workers < 1 || settings.Workers > Math.Min(32, ExperimentLimits.MaxWorkerCount))
            throw new ArgumentException($"Workers must be between 1 and {Math.Min(32, ExperimentLimits.MaxWorkerCount)}.");
        if (settings.DurationSeconds < 1 || settings.DurationSeconds > ExperimentLimits.MaxDurationSeconds)
            throw new ArgumentException("Duration must be between 1 and 60 seconds.");
        if (settings.AllocationMiB is < 1 or > 256 || settings.RetainedMiB is < 1 or > 64 ||
            settings.RetainedMiB > settings.AllocationMiB)
            throw new ArgumentException("Aggregate payload budgets: allocation 1-256 MiB; retention 1-64 MiB, not exceeding allocation.");
    }

    public sealed class Counts
    {
        public long[] Entered { get; } = new long[MethodNames.Length];
        public long[] Completed { get; } = new long[MethodNames.Length];
        public long[] Allocated { get; } = new long[MethodNames.Length];
        public long Begin(int index) { Entered[index]++; return GC.GetAllocatedBytesForCurrentThread(); }
        public void End(int index, long before) { Completed[index]++; Allocated[index] += GC.GetAllocatedBytesForCurrentThread() - before; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long Arithmetic(Counts counts, long seed)
    {
        var before = counts.Begin(0);
        var value = seed;
        for (var i = 0; i < 200_000; i++)
            value = unchecked((value * 6364136223846793005L + i) ^ (value >> 11));
        counts.End(0, before);
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long Checksum(Counts counts, long seed)
    {
        var before = counts.Begin(1);
        for (var i = 0; i < 12_000; i++)
            seed = unchecked((seed << 5) ^ (seed >> 3) ^ i);
        counts.End(1, before);
        return seed;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static byte[] AllocateSmall(Counts counts)
    {
        var before = counts.Begin(2);
        var bytes = new byte[4096];
        bytes.AsSpan().Fill(0x5a);
        counts.End(2, before);
        return bytes;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static byte[] AllocateLarge(Counts counts)
    {
        var before = counts.Begin(3);
        var bytes = new byte[128 * 1024];
        bytes.AsSpan().Fill(0x6b);
        counts.End(3, before);
        return bytes;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static byte[] RetainPayload(Counts counts)
    {
        var before = counts.Begin(4);
        var bytes = new byte[256 * 1024];
        bytes.AsSpan().Fill(0x7c);
        counts.End(4, before);
        return bytes;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void WaitStep(Counts counts)
    {
        var before = counts.Begin(5);
        Thread.Sleep(2);
        counts.End(5, before);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long MixedPipeline(Counts counts, long seed, bool allocate)
    {
        var before = counts.Begin(6);
        var result = Arithmetic(counts, seed);
        if (allocate)
            GC.KeepAlive(AllocateSmall(counts));
        WaitStep(counts);
        counts.End(6, before);
        return result;
    }
}
