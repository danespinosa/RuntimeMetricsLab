using System.Text.Json;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Collector.Windows;

public sealed class WindowsApprovalStore : IWindowsApprovalStore
{
    private readonly string _root;
    public WindowsApprovalStore(string root) { _root = root; Directory.CreateDirectory(root); }
    public static string PathFor(string labId, Guid operationId) =>
        Path.Combine(AttributionPaths.WindowsDataRoot(labId), "profiler-approvals", operationId.ToString("N") + ".json");
    public WindowsProfilerApproval[] Load() => Directory.EnumerateFiles(_root, "*.json")
        .Select(WindowsApprovalFiles.Read<WindowsProfilerApproval>).ToArray();
    public void Save(WindowsProfilerApproval approval)
    {
        var path = Path.Combine(_root, approval.OperationId.ToString("N") + ".json");
        WindowsApprovalFiles.Write(path, approval);
    }
}
