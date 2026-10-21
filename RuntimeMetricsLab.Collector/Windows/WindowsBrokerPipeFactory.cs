using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RuntimeMetricsLab.Collector.Windows;

public sealed record BrokerPipeBoundary(string OwnerSid, string MandatoryLabel, string[] AllowedPrincipals,
    bool ProtectedDacl);

[SupportedOSPlatform("windows")]
public static class WindowsBrokerPipeFactory
{
    private const uint KernelObject = 6;
    private const uint LabelInformation = 0x10;
    private const string MediumLabel = "S:(ML;;NW;;;ME)";

    public static NamedPipeServerStream Create(string pipeName, SecurityIdentifier owner)
    {
        if (!pipeName.StartsWith("RuntimeMetricsLab.", StringComparison.Ordinal) ||
            pipeName.Length > 240 || pipeName.IndexOfAny(['\\', '/']) >= 0)
            throw new ArgumentException("Expected a local RuntimeMetricsLab pipe name.");
        var security = new PipeSecurity();
        security.SetOwner(owner);
        security.SetGroup(owner);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        // A full SACL at CreateNamedPipe takes the audit-sensitive creation path.
        // Create only the owner/DACL, then update LABEL_SECURITY_INFORMATION using
        // this owned handle's WRITE_OWNER right, never ACCESS_SYSTEM_SECURITY.
        var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security,
            HandleInheritability.None, PipeAccessRights.TakeOwnership);
        try
        {
            SetMediumLabel(pipe.SafePipeHandle);
            Verify(pipe.SafePipeHandle, owner);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public static BrokerPipeBoundary Verify(SafePipeHandle handle, SecurityIdentifier owner)
    {
        Check(GetSecurityInfo(handle, KernelObject, 1 | 4 | LabelInformation,
            out _, out _, out _, out _, out var descriptor), "Read owned broker pipe boundary");
        try
        {
            var size = GetSecurityDescriptorLength(descriptor);
            if (size is < 20 or > 16384) throw new InvalidDataException("Unexpected broker pipe security descriptor size.");
            var bytes = new byte[size];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            if (security.Owner is null || !owner.Equals(security.Owner) || security.DiscretionaryAcl is not { Count: 2 } dacl ||
                (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
                throw new UnauthorizedAccessException("Broker pipe owner/DACL does not match the required restricted boundary.");
            var principals = new List<string>();
            foreach (GenericAce entry in dacl)
            {
                if (entry is not CommonAce { AceQualifier: AceQualifier.AccessAllowed, IsCallback: false } ace ||
                    ace.AceFlags != AceFlags.None || ace.AccessMask != (int)PipeAccessRights.FullControl ||
                    (!ace.SecurityIdentifier.Equals(owner) && !ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid)))
                    throw new UnauthorizedAccessException("Broker pipe grants unexpected principal rights.");
                principals.Add(ace.SecurityIdentifier.Value);
            }
            if (principals.Distinct(StringComparer.Ordinal).Count() != 2)
                throw new UnauthorizedAccessException("Broker pipe must authorize only the owner and SYSTEM.");
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, 1, LabelInformation, out var labelText, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Read broker pipe integrity label");
            try
            {
                var label = Marshal.PtrToStringUni(labelText);
                if (label != MediumLabel)
                    throw new UnauthorizedAccessException("Broker pipe must have exactly a medium no-write-up mandatory label.");
                return new(owner.Value, label, principals.ToArray(), true);
            }
            finally { LocalFree(labelText); }
        }
        finally { LocalFree(descriptor); }
    }

    private static void SetMediumLabel(SafePipeHandle handle)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(MediumLabel, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Build the broker pipe integrity label");
        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out var present, out var label, out _) || !present || label == 0)
                throw new InvalidDataException("The broker pipe mandatory-label descriptor is invalid.");
            Check(SetSecurityInfo(handle, KernelObject, LabelInformation, 0, 0, 0, label),
                "Set only the owned broker pipe's medium integrity label");
        }
        finally { LocalFree(descriptor); }
    }

    public static int ClientProcessId(SafePipeHandle handle)
    {
        if (!GetNamedPipeClientProcessId(handle, out var id))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Read broker pipe client identity");
        return checked((int)id);
    }
    public static int ServerProcessId(SafePipeHandle handle)
    {
        if (!GetNamedPipeServerProcessId(handle, out var id))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Read broker pipe server identity");
        return checked((int)id);
    }
    private static void Check(uint error, string operation)
    {
        if (error != 0) throw new Win32Exception((int)error, operation);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(nint descriptor, uint revision, uint information, out nint text, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(nint descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present,
        out nint sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(nint descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafePipeHandle handle, uint type, uint information, nint owner, nint group, nint dacl, nint sacl);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, uint type, uint information,
        out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint allocation);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint id);
}
