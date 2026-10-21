using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using RuntimeMetricsLab.Experiments.Attribution;
using RuntimeMetricsLab.Collector.Windows;
using RuntimeMetricsLab.Contracts.Attribution;

[SupportedOSPlatform("windows")]
internal static class WindowsPipeProbe
{
    public static async Task<int> Run()
    {
        using var process = Process.GetCurrentProcess();
        if (WindowsToken.IsElevated(process.Handle))
            throw new InvalidOperationException("Pipe-only validation must remain non-elevated.");
        using var identity = WindowsIdentity.GetCurrent();
        var name = "RuntimeMetricsLab.PipeProbe." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = WindowsBrokerPipeFactory.Create(name, identity.User!);
        var boundary = WindowsBrokerPipeFactory.Verify(pipe.SafePipeHandle, identity.User!);
        var collisionRejected = false;
        try
        {
            using var collision = WindowsBrokerPipeFactory.Create(name, identity.User!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            collisionRejected = true;
        }
        if (!collisionRejected) throw new InvalidOperationException("First-instance collision was not rejected.");
        var listening = pipe.WaitForConnectionAsync(deadline.Token);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--pipe-client");
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(process.Id.ToString());
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Non-elevated pipe client did not start.");
        try
        {
            var childOutput = child.StandardOutput.ReadToEndAsync(deadline.Token);
            var childError = child.StandardError.ReadToEndAsync(deadline.Token);
            await listening;
            if (WindowsBrokerPipeFactory.ClientProcessId(pipe.SafePipeHandle) != child.Id)
                throw new UnauthorizedAccessException("Unexpected native pipe client identity.");
            var request = await WindowsCollectorProtocol.ReadAsync<string>(pipe, deadline.Token);
            if (request != "pipe-only-message") throw new InvalidDataException("Unexpected pipe message.");
            await WindowsCollectorProtocol.WriteAsync(pipe, "pipe-only-response", deadline.Token);
            await child.WaitForExitAsync(deadline.Token);
            if (child.ExitCode != 0) throw new InvalidOperationException("Pipe client failed: " + await childError);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Kind = "production-pipe-factory", Runtime = Environment.Version.ToString(),
                Elevated = false, EtwEnabled = false, PrivilegesChanged = false,
                ServerProcessId = process.Id, ChildProcessId = child.Id, Boundary = boundary,
                SameOwnerChildExchange = true, FirstInstanceCollisionRejected = collisionRejected,
                ChildEvidence = await childOutput,
                CrossElevationHandshake = "Not exercised; requires a fresh user-approved collector."
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
        }
    }

    public static async Task<int> Client(string name, int expectedServer)
    {
        using var process = Process.GetCurrentProcess();
        if (WindowsToken.IsElevated(process.Handle)) throw new InvalidOperationException("Client must remain non-elevated.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        if (WindowsBrokerPipeFactory.ServerProcessId(pipe.SafePipeHandle) != expectedServer)
            throw new UnauthorizedAccessException("Unexpected native pipe server identity.");
        await WindowsCollectorProtocol.WriteAsync(pipe, "pipe-only-message", deadline.Token);
        if (await WindowsCollectorProtocol.ReadAsync<string>(pipe, deadline.Token) != "pipe-only-response")
            throw new InvalidDataException("Pipe response failed.");
        Console.WriteLine("Non-elevated child verified server PID and exchanged the bounded framed message.");
        return 0;
    }

    public static int ReproduceLegacy()
    {
        using var process = Process.GetCurrentProcess();
        if (WindowsToken.IsElevated(process.Handle))
            throw new InvalidOperationException("This pipe-only reproduction must remain non-elevated.");
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User!.Value;
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm($"O:{owner}G:{owner}D:P(A;;GA;;;SY)(A;;GA;;;{owner})S:(ML;;NW;;;ME)");
        try
        {
            using var pipe = NamedPipeServerStreamAcl.Create("RuntimeMetricsLab.LegacyPipeProbe." + Guid.NewGuid().ToString("N"),
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
                0, 0, security);
            Console.WriteLine(JsonSerializer.Serialize(new { Kind = "legacy-pipe-only", Reproduced = false, Elevated = false, EtwEnabled = false }));
            return 2;
        }
        catch (IOException exception)
        {
            var error = exception.HResult & 0xffff;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Kind = "legacy-pipe-only", Reproduced = error == 1314, NativeError = error, exception.Message,
                Runtime = Environment.Version.ToString(), Elevated = false, EtwEnabled = false, PrivilegesChanged = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            return error == 1314 ? 0 : 3;
        }
    }
}
