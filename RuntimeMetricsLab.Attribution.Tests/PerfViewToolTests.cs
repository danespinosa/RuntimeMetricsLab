using RuntimeMetricsLab.Collector.Windows;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Attribution.Tests;

public sealed class PerfViewToolTests
{
    [Fact]
    public void Command_UsesStockBoundedAttachWithNoConsentBypassOrElevatedWorkloadCommand()
    {
        var session = PerfViewTool.SessionName("6c1746d62e", Guid.NewGuid());
        var arguments = PerfViewTool.CollectArguments(Path.GetTempPath(), session, 123, 12);
        Assert.Contains("/FocusProcess:123", arguments);
        Assert.Contains("/MaxCollectSec:12", arguments);
        Assert.Contains("/KernelEvents:Process,Thread,ImageLoad,Profile", arguments);
        Assert.Contains("/ClrEvents:Default,AllocationSampling", arguments);
        Assert.Contains("/BufferSizeMB:256", arguments);
        Assert.Contains("/RundownTimeout:120", arguments);
        Assert.Contains("/Providers:*RuntimeMetricsLab-Attribution", arguments);
        Assert.Equal("collect", arguments[^1]);
        Assert.DoesNotContain(arguments, item => item.Contains("AcceptEULA") || item.Contains("ThreadTime") ||
            item.Equals("run") || item.Equals("stop") || item.Equals("abort"));
    }

    [Fact]
    public void EveryCapture_HasItsOwnValidatedSessionName()
    {
        Assert.NotEqual(PerfViewTool.SessionName("6c1746d62e", Guid.NewGuid()),
            PerfViewTool.SessionName("6c1746d62e", Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => PerfViewTool.SessionName("6c1746d62e", Guid.Empty));
        Assert.Throws<ArgumentException>(() => PerfViewTool.SessionName("../escape", Guid.NewGuid()));
    }

    [Theory]
    [InlineData("PerfViewSession")]
    [InlineData("PerfViewGCHeapSession")]
    [InlineData("RuntimeMetricsLab.PerfView.6c1746d62e.previous")]
    [InlineData("ETWEventTrigger-existing")]
    public void ExistingProfiler_IsRejectedRatherThanStopped(string name) =>
        Assert.Throws<InvalidOperationException>(() => PerfViewTool.CheckSessionList(
            $"Active Session Names\n    {name}\n[1 Total Active sessions: (See Log)]", "new-session"));

    [Fact]
    public void IncompleteSessionProbe_IsNotTreatedAsAnEmptyMachine() =>
        Assert.Throws<InvalidDataException>(() => PerfViewTool.CheckSessionList("probe failed", "new-session"));

    [Fact]
    public void NormalSystemSessions_DoNotRequireGlobalCleanup() =>
        PerfViewTool.CheckSessionList("Active Session Names\n    EventLog-System\n[1 Total Active sessions: (See Log)]", "new-session");

    [Fact]
    public void MissingEulaAndNonzeroExit_AreExplicitFailures()
    {
        Assert.Contains("review and accept", Assert.Throws<InvalidOperationException>(() =>
            PerfViewTool.RequireSuccess(-3, "The PerfView EULA has not been accepted.")).Message);
        Assert.Throws<InvalidOperationException>(() => PerfViewTool.RequireSuccess(1, "collection failed"));
    }

    [Fact]
    public void StockArtifactsAndBudgets_DoNotChangeLegacyLinuxDefaults()
    {
        Assert.Equal(34, AttributionBudgets.CaptureSeconds(4, AttributionMode.Linux));
        Assert.Equal(12, AttributionBudgets.CaptureSeconds(4, AttributionMode.Windows));
        Assert.Equal(25, AttributionBudgets.FinalizationSeconds(AttributionMode.Linux));
        Assert.Equal(300, AttributionBudgets.FinalizationSeconds(AttributionMode.Windows));
        var stock = AttributionArtifacts.CollectorFiles(AttributionTraceFormat.PerfViewEtw, false);
        Assert.Contains("perfview.json", stock);
        Assert.Contains("perfview.log", stock);
        Assert.Contains("session-list.log", stock);
        Assert.DoesNotContain("perfview.json", AttributionArtifacts.CollectorFiles(AttributionTraceFormat.UniversalNetTraceV6, false));
    }
}
