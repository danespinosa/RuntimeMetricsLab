using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RuntimeMetricsLab.Collector.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsStorageSecurity
{
    public static void ProtectUserDirectory(string path, SecurityIdentifier owner)
    {
        RejectReparsePoints(path);
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(owner);
        Grant(security, owner, FileSystemRights.FullControl);
        Grant(security, new(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl);
        Grant(security, new(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public static void CreateProtectedCaptureRoot(string path, SecurityIdentifier reader)
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(common) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Elevated captures must use the fixed local ProgramData lab root.");
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var current = common;
        foreach (var segment in Path.GetRelativePath(common, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoints(current);
            if (Directory.Exists(current))
            {
                var existing = new DirectoryInfo(current).GetAccessControl();
                var owner = existing.GetOwner(typeof(SecurityIdentifier)) ??
                    throw new InvalidOperationException("Cannot verify existing collector storage owner.");
                if (!owner.Equals(admin) && !owner.Equals(system))
                    throw new InvalidOperationException("Existing collector storage is not administrator-owned; inspect it manually before retrying.");
                foreach (FileSystemAccessRule rule in existing.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    var write = rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete |
                        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.DeleteSubdirectoriesAndFiles);
                    if (rule.AccessControlType == AccessControlType.Allow && write != 0 &&
                        !rule.IdentityReference.Equals(admin) && !rule.IdentityReference.Equals(system))
                        throw new InvalidOperationException("Existing collector storage permits non-administrator modification; refusing privileged writes.");
                }
            }
            else
            {
                var security = new DirectorySecurity();
                security.SetOwner(admin);
                security.SetAccessRuleProtection(true, false);
                Grant(security, admin, FileSystemRights.FullControl);
                Grant(security, system, FileSystemRights.FullControl);
                Grant(security, reader, FileSystemRights.ReadAndExecute);
                new DirectoryInfo(current).Create(security);
            }
        }
    }

    public static void RejectReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse points are not accepted in collector storage/configuration paths.");
            current = Path.GetDirectoryName(current);
        }
    }

    public static void VerifyUserRegistration(string path, SecurityIdentifier owner)
    {
        RejectReparsePoints(path);
        var security = new FileInfo(path).GetAccessControl();
        if (!owner.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("Registration must be owned by the expected non-elevated user.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (!sid.Equals(owner) && !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) &&
                !sid.IsWellKnown(WellKnownSidType.LocalSystemSid))
                throw new UnauthorizedAccessException("Collector registration grants another principal access.");
        }
    }

    private static void Grant(DirectorySecurity security, SecurityIdentifier sid, FileSystemRights rights) =>
        security.AddAccessRule(new(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
}
