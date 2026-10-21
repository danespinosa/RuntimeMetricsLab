using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Runtime.Versioning;
using RuntimeMetricsLab.Collector.Windows;
using RuntimeMetricsLab.Contracts.Attribution;
using RuntimeMetricsLab.Experiments.Attribution;

namespace RuntimeMetricsLab.Attribution.Tests;

public sealed class WindowsBrokerPipeTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task NativeFactory_UsesOwnerDaclAndMediumLabelWithoutAuditPrivileges()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.GetCurrentProcess();
        Assert.False(WindowsToken.IsElevated(process.Handle));
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User!;
        var name = "RuntimeMetricsLab.PipeRegression." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var server = WindowsBrokerPipeFactory.Create(name, owner))
        {
            var boundary = WindowsBrokerPipeFactory.Verify(server.SafePipeHandle, owner);
            Assert.Equal(owner.Value, boundary.OwnerSid);
            Assert.Equal("S:(ML;;NW;;;ME)", boundary.MandatoryLabel);
            Assert.True(boundary.ProtectedDacl);
            Assert.Equal(2, boundary.AllowedPrincipals.Length);
            Assert.Contains(owner.Value, boundary.AllowedPrincipals);
            Assert.Contains("S-1-5-18", boundary.AllowedPrincipals);
            Assert.DoesNotContain("S-1-1-0", boundary.AllowedPrincipals);
            Assert.Throws<UnauthorizedAccessException>(() => WindowsBrokerPipeFactory.Verify(server.SafePipeHandle,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
            var collision = Record.Exception(() => WindowsBrokerPipeFactory.Create(name, owner));
            Assert.True(collision is IOException or UnauthorizedAccessException);
            Assert.Contains(collision!.HResult & 0xffff, new[] { 5, 183, 231 });

            for (var index = 0; index < 2; index++)
            {
                var ready = false;
                var listening = WindowsBrokerStartup.ListenBeforeReady(
                    () => server.WaitForConnectionAsync(deadline.Token), () => ready = true);
                Assert.True(ready);
                await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(deadline.Token);
                await listening;
                Assert.Equal(process.Id, WindowsBrokerPipeFactory.ClientProcessId(server.SafePipeHandle));
                Assert.Equal(process.Id, WindowsBrokerPipeFactory.ServerProcessId(client.SafePipeHandle));
                var serverRead = WindowsCollectorProtocol.ReadAsync<string>(server, deadline.Token);
                await WindowsCollectorProtocol.WriteAsync(client, $"message-{index}", deadline.Token);
                Assert.Equal($"message-{index}", await serverRead);
                var clientRead = WindowsCollectorProtocol.ReadAsync<string>(client, deadline.Token);
                await WindowsCollectorProtocol.WriteAsync(server, $"response-{index}", deadline.Token);
                Assert.Equal($"response-{index}", await clientRead);
                server.Disconnect();
            }
        }
        // Closing the exact owned handle releases the first-instance name.
        await using var replacement = WindowsBrokerPipeFactory.Create(name, owner);
        Assert.Equal("S:(ML;;NW;;;ME)", WindowsBrokerPipeFactory.Verify(replacement.SafePipeHandle, owner).MandatoryLabel);
    }

    [Fact]
    public async Task ReadySignal_IsNeverEmittedWhenListeningFailsOrIsCancelled()
    {
        var ready = false;
        await Assert.ThrowsAsync<IOException>(() => WindowsBrokerStartup.ListenBeforeReady(
            () => throw new IOException("Native listen failed"), () => ready = true));
        Assert.False(ready);
        await Assert.ThrowsAsync<IOException>(() => WindowsBrokerStartup.ListenBeforeReady(
            () => Task.FromException(new IOException("Faulted native listen")), () => ready = true));
        Assert.False(ready);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsBrokerStartup.ListenBeforeReady(
            () => Task.FromCanceled(new CancellationToken(true)), () => ready = true));
        Assert.False(ready);
    }

    [Fact]
    public void FailureReport_PreservesNativeCodeStageAndExactReason()
    {
        var identity = new WindowsProfilerActivation(Guid.NewGuid(), Guid.NewGuid(), 123, DateTimeOffset.UtcNow);
        var native = new IOException("A required privilege is not held by the client.", unchecked((int)0x80070522));
        var failure = WindowsBrokerStartup.Failure(identity, WindowsBrokerStartupStage.PipeCreation, native);
        Assert.Equal(1314, failure.NativeErrorCode);
        Assert.Equal(unchecked((int)0x80070522), failure.HResult);
        var text = WindowsBrokerStartup.Describe(failure);
        Assert.Contains("PipeCreation", text);
        Assert.Contains(native.Message, text);
        Assert.Contains("Win32 1314", text);
        Assert.Contains("0x80070522", text);
    }
}
