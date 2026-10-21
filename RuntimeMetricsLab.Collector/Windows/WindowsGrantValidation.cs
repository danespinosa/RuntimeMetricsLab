using System.Runtime.Versioning;
using System.Text.Json;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Collector.Windows;

public static class WindowsGrantValidation
{
    public static bool Matches(WindowsCollectorRegistration launch, WindowsCollectorRegistration current,
        WindowsProfilerGrant? authority, DateTimeOffset now) =>
        launch.SchemaVersion == WindowsCollectorProtocol.RegistrationSchema &&
        current.SchemaVersion == WindowsCollectorProtocol.RegistrationSchema && launch.Generation != Guid.Empty &&
        launch.Generation == current.Generation && launch.OwnerProcessId == current.OwnerProcessId &&
        launch.OwnerProcessStartUtc == current.OwnerProcessStartUtc &&
        launch.Grant is { Revoked: false } expected && authority is { Revoked: false } &&
        expected == authority && authority.RegistrationGeneration == launch.Generation &&
        authority.AttemptId != Guid.Empty && authority.OperationId != Guid.Empty && authority.ExpiresUtc > now;

    public static bool ApprovalActive(WindowsCollectorRegistration launch, WindowsProfilerApproval? approval, DateTimeOffset now) =>
        launch.Grant is { } grant && approval is not null && !approval.IsTerminal && !approval.ReusedBroker &&
        approval.Phase is ProfilerApprovalPhase.AwaitingElevation or ProfilerApprovalPhase.WaitingProfilerReady or ProfilerApprovalPhase.Ready &&
        approval.OperationId == grant.OperationId && approval.AttemptId == grant.AttemptId &&
        approval.RegistrationGeneration == launch.Generation && approval.DeadlineUtc > now &&
        (approval.ClaimedByCapture || approval.ClientLeaseExpiresUtc > now);

    [SupportedOSPlatform("windows")]
    public static bool IsCurrent(WindowsCollectorRegistration launch)
    {
        if (launch.Grant is null) return false;
        var root = AttributionPaths.WindowsDataRoot(launch.LabId);
        var registrationPath = Path.Combine(root, "collector-registration.json");
        var grantPath = WindowsCollectorProtocol.GrantFile(launch.LabId, launch.Grant.AttemptId);
        var approvalPath = WindowsApprovalStore.PathFor(launch.LabId, launch.Grant.OperationId);
        if (!File.Exists(registrationPath) || !File.Exists(grantPath) || !File.Exists(approvalPath)) return false;
        WindowsStorageSecurity.RejectReparsePoints(registrationPath);
        WindowsStorageSecurity.RejectReparsePoints(grantPath);
        WindowsStorageSecurity.RejectReparsePoints(approvalPath);
        var current = WindowsApprovalFiles.Read<WindowsCollectorRegistration>(registrationPath);
        var authority = WindowsApprovalFiles.Read<WindowsProfilerGrant>(grantPath);
        var approval = WindowsApprovalFiles.Read<WindowsProfilerApproval>(approvalPath);
        return Matches(launch, current, authority, DateTimeOffset.UtcNow) &&
            ApprovalActive(launch, approval, DateTimeOffset.UtcNow);
    }
}
