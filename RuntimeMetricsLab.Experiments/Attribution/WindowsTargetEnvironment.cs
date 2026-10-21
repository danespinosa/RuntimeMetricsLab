using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Experiments.Attribution;

[SupportedOSPlatform("windows")]
public sealed class WindowsTargetEnvironment : IAttributionTargetEnvironment
{
    public TargetIdentity ReadIdentity()
    {
        using var process = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        if (WindowsToken.IsElevated(process.Handle))
            throw new InvalidOperationException("The Windows workload must run non-elevated. Elevate only the explicit collector.");
        var metadata = TargetEnvironmentMetadata.Build();
        metadata["platform"] = "Native Windows process";
        metadata["windows-version"] = Environment.OSVersion.Version.ToString();
        metadata["active-processor-groups"] = Native.GetActiveProcessorGroupCount().ToString();
        ushort groupCount = 64;
        var groups = new ushort[groupCount];
        if (Native.GetProcessGroupAffinity(process.Handle, ref groupCount, groups))
        {
            metadata["process-processor-groups"] = string.Join(",", groups.Take(groupCount));
            if (groupCount == 1 && Native.GetProcessAffinityMask(process.Handle, out var processMask, out var systemMask))
            {
                metadata["single-group-affinity-mask"] = $"0x{processMask:x}";
                metadata["single-group-system-mask"] = $"0x{systemMask:x}";
            }
            else metadata["single-group-affinity-mask"] = "Not a complete cross-group affinity representation.";
        }
        else metadata["process-processor-groups"] = Error("GetProcessGroupAffinity");

        if (Native.GetProcessDefaultCpuSets(process.Handle, null, 0, out var required) || Marshal.GetLastPInvokeError() == 122)
        {
            if (required > 4096) throw new InvalidOperationException("Unexpected CPU-set metadata size.");
            var ids = new uint[required];
            metadata["process-default-cpu-sets"] = required == 0 ? "No explicit process CPU-set selection." :
                Native.GetProcessDefaultCpuSets(process.Handle, ids, required, out _)
                    ? string.Join(",", ids) : Error("GetProcessDefaultCpuSets");
        }
        else metadata["process-default-cpu-sets"] = Error("GetProcessDefaultCpuSets");

        if (Native.IsProcessInJob(process.Handle, nint.Zero, out var inJob))
        {
            metadata["in-job"] = inJob.ToString();
            if (inJob)
            {
                var rate = new CpuRateInformation();
                metadata["innermost-job-cpu-rate"] = Native.QueryInformationJobObject(nint.Zero, 15, ref rate,
                    (uint)Marshal.SizeOf<CpuRateInformation>(), nint.Zero)
                    ? $"flags=0x{rate.ControlFlags:x}; raw-rate-or-weight={rate.CpuRate}. Ancestor job limits are not inferred."
                    : Error("QueryInformationJobObject");
            }
        }
        else metadata["in-job"] = Error("IsProcessInJob");
        metadata["limits"] = "The lab creates no CPU/memory quota or job object. Runtime ProcessorCount is the normalization denominator; affinity, CPU sets and job metadata are supplemental, not proof that all inherited restrictions are known.";
        return new(Guid.NewGuid(), Environment.ProcessId, Stopwatch.GetTimestamp(), null, Environment.Version.ToString(),
            Environment.ProcessorCount, metadata, AttributionPlatform.WindowsProcess,
            process.StartTime.ToUniversalTime(), identity.User?.Value);
    }

    private static string Error(string operation) => $"{operation} unavailable: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateInformation { public uint ControlFlags; public uint CpuRate; }

    private static class Native
    {
        [DllImport("kernel32.dll")] internal static extern ushort GetActiveProcessorGroupCount();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessGroupAffinity(nint process, ref ushort count, [Out] ushort[] groups);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessAffinityMask(nint process, out nuint processMask, out nuint systemMask);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessDefaultCpuSets(nint process, [Out] uint[]? ids, uint count, out uint required);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(nint job, int informationClass, ref CpuRateInformation information,
            uint length, nint returnLength);
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsToken
{
    public static string UserSid(nint processHandle)
    {
        if (!OpenProcessToken(processHandle, 8, out var token))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect process owner.");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value ?? throw new InvalidOperationException("Process owner SID is unavailable.");
    }

    public static bool IsElevated(nint processHandle)
    {
        if (!OpenProcessToken(processHandle, 8, out var token))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect the process token.");
        using (token)
        {
            if (!GetTokenInformation(token, 20, out var elevated, sizeof(int), out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read token elevation.");
            return elevated != 0;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out int information,
        int length, out int returnLength);
}
