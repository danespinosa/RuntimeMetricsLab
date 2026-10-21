using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RuntimeMetricsLab.Experiments.Attribution;

public sealed record WindowsProcessSnapshot(int ProcessId, DateTimeOffset StartUtc, string ImagePath, string UserSid, bool Elevated);

[SupportedOSPlatform("windows")]
public static class WindowsProcessIdentity
{
    public static WindowsProcessSnapshot Read(int processId)
    {
        using var handle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot query process identity with limited rights.");
        var image = new StringBuilder(32768);
        var size = image.Capacity;
        if (!QueryFullProcessImageNameW(handle, 0, image, ref size) ||
            !GetProcessTimes(handle, out var creation, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot query process image/start time.");
        var pointer = handle.DangerousGetHandle();
        return new(processId, DateTimeOffset.FromFileTime(creation).ToUniversalTime(), image.ToString(),
            WindowsToken.UserSid(pointer), WindowsToken.IsElevated(pointer));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
