using System.Globalization;

namespace RuntimeMetricsLab.Collector;

public static class CollectorBoundary
{
    public const ulong RuntimeCapabilities = 0x0000004000080002;

    public static void Verify()
    {
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("The collector requires Linux.");
        var status = File.ReadLines("/proc/self/status").Where(line => line.Contains(':'))
            .Select(line => line.Split(':', 2)).ToDictionary(parts => parts[0], parts => parts[1].Trim());
        foreach (var name in new[] { "CapEff", "CapPrm", "CapBnd" })
            if (ulong.Parse(status[name], NumberStyles.HexNumber, CultureInfo.InvariantCulture) != RuntimeCapabilities)
                throw new InvalidOperationException($"Collector {name} must contain only PERFMON, SYS_PTRACE and DAC_OVERRIDE.");
        if (status["NoNewPrivs"] != "1" || status["CapInh"].Trim('0').Length != 0 || status["CapAmb"].Trim('0').Length != 0)
            throw new InvalidOperationException("Collector must irreversibly drop bootstrap privileges and prohibit new privileges.");
        VerifyMount("/sys/kernel/tracing", "ro");
        VerifyMount("/sys/kernel/tracing/user_events_data", "rw");
        var ownNamespace = new FileInfo("/proc/self/ns/mnt").LinkTarget ??
            throw new IOException("Cannot read collector mount namespace.");
        if (ownNamespace == new FileInfo("/proc/1/ns/mnt").LinkTarget)
            throw new InvalidOperationException("Collector must have its own private mount namespace.");
    }

    private static void VerifyMount(string path, string access)
    {
        var mounts = File.ReadLines("/proc/self/mountinfo").Select(line => line.Split(' '))
            .Where(parts => parts.Length > 7 && parts[4] == path).ToArray();
        if (mounts.Length != 1)
            throw new InvalidOperationException($"Missing unique collector mount: {path}.");
        var mount = mounts[0];
        var separator = Array.IndexOf(mount, "-");
        if (separator < 6 || !mount[5].Split(',').Contains(access) ||
            mount[6..separator].Any(option => option.StartsWith("shared:") || option.StartsWith("master:")))
            throw new InvalidOperationException($"Collector mount {path} must be private and {access}.");
    }
}
