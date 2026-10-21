using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Collector.Windows;

public interface IWindowsElevationLauncher
{
    Task<ElevationLaunchResult> Launch(WindowsCollectorRegistration registration, string immutableLaunchPath);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsElevationLauncher : IWindowsElevationLauncher
{
    public Task<ElevationLaunchResult> Launch(WindowsCollectorRegistration registration, string immutableLaunchPath) =>
        Task.Run(() =>
        {
            try
            {
                var start = new ProcessStartInfo(registration.CollectorExecutable)
                {
                    UseShellExecute = true, Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(registration.CollectorExecutable)!
                };
                start.ArgumentList.Add("--windows-broker");
                start.ArgumentList.Add("--registration");
                start.ArgumentList.Add(immutableLaunchPath);
                start.ArgumentList.Add("--accept-perfview-system-trace");
                using var process = Process.Start(start);
                return process is null ? new(ElevationLaunchKind.Failed, Error: "Windows returned no owned process handle.") :
                    new ElevationLaunchResult(ElevationLaunchKind.Started, process.Id);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                return new(ElevationLaunchKind.Denied);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                return new(ElevationLaunchKind.Failed, Error: exception.Message);
            }
        });
}
