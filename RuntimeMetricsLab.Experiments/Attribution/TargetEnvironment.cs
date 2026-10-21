using System.Diagnostics;
using System.Runtime;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Experiments.Attribution;

public interface IAttributionTargetEnvironment
{
    TargetIdentity ReadIdentity();
}

public sealed class LinuxTargetEnvironment : IAttributionTargetEnvironment
{
    public TargetIdentity ReadIdentity()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The Linux target requires Linux.");
        var metadata = TargetEnvironmentMetadata.Build();
        foreach (var file in new[] { "cpu.max", "cpu.stat", "cpuset.cpus.effective", "memory.max", "memory.current" })
        {
            var path = $"/sys/fs/cgroup/{file}";
            metadata[file] = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        metadata["affinity"] = File.ReadLines("/proc/self/status").FirstOrDefault(line => line.StartsWith("Cpus_allowed_list:"));
        metadata["limits"] = "No lab-imposed container CPU or memory quotas; visible inherited cgroup values above.";
        using var process = Process.GetCurrentProcess();
        return new(Guid.NewGuid(), Environment.ProcessId, Stopwatch.GetTimestamp(),
            new FileInfo("/proc/self/ns/pid").LinkTarget ?? "unavailable", Environment.Version.ToString(),
            Environment.ProcessorCount, metadata, ProcessStartUtc: process.StartTime.ToUniversalTime());
    }
}

internal static class TargetEnvironmentMetadata
{
    public static Dictionary<string, string?> Build() => new()
    {
        ["build"] = "Selected teaching boundaries NoInlining; normal optimized build behavior outside those boundaries.",
        ["jit-optimizer-disabled"] = (typeof(AttributionLessons).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .OfType<DebuggableAttribute>().FirstOrDefault()?.IsJITOptimizerDisabled ?? false).ToString(),
        ["workload-module-mvid"] = typeof(AttributionLessons).Module.ModuleVersionId.ToString(),
        ["server-gc"] = GCSettings.IsServerGC.ToString(),
        ["tiered-compilation-setting"] = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default"
    };
}
