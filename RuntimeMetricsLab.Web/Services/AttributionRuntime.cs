using System.Diagnostics;
using System.Security.Principal;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Web.Services;

public sealed class AttributionRuntime
{
    public AttributionMode Mode { get; }
    public AttributionPaths Paths { get; }
    public string WorkerPath { get; }

    public AttributionRuntime(IConfiguration configuration)
    {
        Mode = AttributionModes.Parse(configuration["RuntimeMetricsLab:Mode"]);
        if (Mode == AttributionMode.Windows)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows attribution requires native Windows.");
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Keep the web UI non-elevated; launch only the explicit Windows collector through UAC.");
            var labId = AttributionPaths.ValidateLabId(configuration["Attribution:LabId"] ??
                throw new InvalidOperationException("The Windows lab identity is required."));
            Paths = new(configuration["Attribution:DataRoot"] ?? AttributionPaths.WindowsDataRoot(labId),
                configuration["Attribution:CapturesRoot"] ?? AttributionPaths.WindowsCaptureRoot(labId));
            WorkerPath = Path.GetFullPath(configuration["Attribution:WorkerPath"] ??
                Path.Combine(AppContext.BaseDirectory, "analysis", "RuntimeMetricsLab.Analysis.Worker.exe"));
        }
        else if (Mode == AttributionMode.Linux)
        {
            Paths = AttributionPaths.Linux();
            WorkerPath = "/app/analysis/RuntimeMetricsLab.Analysis.Worker.dll";
        }
        else throw new InvalidOperationException("The attribution runtime is not used in standard mode.");
    }

    public ProcessStartInfo AnalysisProcess(Guid id)
    {
        if (!File.Exists(WorkerPath))
            throw new FileNotFoundException("The isolated analysis worker has not been built/published.", WorkerPath);
        var start = new ProcessStartInfo(Mode == AttributionMode.Windows ? WorkerPath : "/usr/share/dotnet/dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Paths.Job(id)
        };
        if (Mode == AttributionMode.Linux) start.ArgumentList.Add(WorkerPath);
        foreach (var argument in new[] { "--job", id.ToString("N"), "--data-root", Paths.DataRoot, "--capture-root", Paths.CapturesRoot })
            start.ArgumentList.Add(argument);
        start.Environment.Clear();
        Directory.CreateDirectory(Paths.TemporaryRoot);
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        if (Mode == AttributionMode.Windows)
        {
            start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            start.Environment["TEMP"] = Paths.TemporaryRoot;
            start.Environment["TMP"] = Paths.TemporaryRoot;
        }
        else start.Environment["HOME"] = "/tmp";
        return start;
    }
}
