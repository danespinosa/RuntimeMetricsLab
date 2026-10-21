using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RuntimeMetricsLab.Contracts.Attribution;
using RuntimeMetricsLab.Experiments.Attribution;

namespace RuntimeMetricsLab.Collector.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsCollectorBroker
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 4 || args[0] != "--windows-broker" || args[1] != "--registration" ||
            args[3] != "--accept-perfview-system-trace")
            throw new ArgumentException("Windows launcher requires a current registration and explicit --accept-perfview-system-trace consent.");
        using var self = Process.GetCurrentProcess();
        if (!WindowsToken.IsElevated(self.Handle))
            throw new InvalidOperationException("Start only this collector through the provided consent/UAC launcher.");
        var path = Path.GetFullPath(args[2]);
        WindowsStorageSecurity.RejectReparsePoints(path);
        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("Oversized collector registration.");
        var registration = JsonSerializer.Deserialize<WindowsCollectorRegistration>(await File.ReadAllTextAsync(path)) ??
            throw new InvalidDataException("Missing collector registration.");
        using var identity = WindowsIdentity.GetCurrent();
        WindowsStorageSecurity.VerifyUserRegistration(path, identity.User ??
            throw new InvalidOperationException("Current owner SID is unavailable."));
        if (registration.SchemaVersion != WindowsCollectorProtocol.RegistrationSchema || registration.Grant is null || identity.User?.Value != registration.OwnerSid ||
            registration.ControlKey.Length < 32 || registration.TargetKey.Length < 32 ||
            !Uri.TryCreate(registration.TargetUrl, UriKind.Absolute, out var targetUrl) || !targetUrl.IsLoopback ||
            !Path.GetFullPath(registration.CollectorExecutable).Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Collector registration identity/path/credential is invalid.");
        var expected = WindowsCollectorProtocol.LaunchFile(registration.LabId, registration.Grant.AttemptId);
        if (!path.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Collector registration is outside this lab's private local configuration.");
        if (!WindowsGrantValidation.IsCurrent(registration))
            throw new InvalidOperationException("This profiler approval was cancelled, expired, or replaced. No collector session will start.");
        using var owner = Process.GetProcessById(registration.OwnerProcessId);
        var ownerIdentity = WindowsProcessIdentity.Read(registration.OwnerProcessId);
        if (ownerIdentity.StartUtc != registration.OwnerProcessStartUtc || ownerIdentity.Elevated ||
            ownerIdentity.UserSid != registration.OwnerSid ||
            !string.Equals(ownerIdentity.ImagePath, registration.CollectorExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The non-elevated collector control proxy changed identity.");
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options => options.SingleLine = true));
        var logger = loggerFactory.CreateLogger("WindowsCollectorBroker");
        if (!Uri.TryCreate(registration.ControlUrl, UriKind.Absolute, out var controlUrl) || !controlUrl.IsLoopback ||
            controlUrl.Scheme is not ("http" or "https"))
            throw new InvalidDataException("A fixed local control authority is required.");
        using var authority = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2), AllowAutoRedirect = false })
        {
            BaseAddress = controlUrl, Timeout = TimeSpan.FromSeconds(3)
        };
        authority.DefaultRequestHeaders.Add("X-Lab-Key", registration.ControlKey);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromTicks(Math.Max(1, Math.Min(
            WindowsApprovalCoordinator.BrokerLifetime.Ticks, (registration.Grant.ExpiresUtc - DateTimeOffset.UtcNow).Ticks))));
        Task? grantMonitor = null;
        var startupStage = WindowsBrokerStartupStage.Activation;
        try
        {
            // Late/cancelled approvals must pass live authority before any
            // privileged collector initialization, including this owned pipe.
            await ConfirmActivation(lifetime.Token);
            startupStage = WindowsBrokerStartupStage.PipeCreation;
            await using var pipe = WindowsBrokerPipeFactory.Create(
                WindowsCollectorProtocol.PipeName(registration.LabId, registration.Generation, registration.Grant.AttemptId),
                new SecurityIdentifier(registration.OwnerSid));
            startupStage = WindowsBrokerStartupStage.PerfViewTool;
            using var verifiedTool = PerfViewTool.OpenVerified(registration.CollectorExecutable);
            startupStage = WindowsBrokerStartupStage.CaptureStorage;
            await using var captures = new PerfViewCaptureManager(registration, logger);
            grantMonitor = MonitorGrant();
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; lifetime.Cancel(); };
            startupStage = WindowsBrokerStartupStage.PipeListening;
            var connection = WindowsBrokerStartup.ListenBeforeReady(
                () => pipe.WaitForConnectionAsync(lifetime.Token),
                () => logger.LogInformation(
                    "Consented stock PerfView launcher ready for lab {LabId}; private pipe verified and listening. PerfView is not launched until capture admission.",
                    registration.LabId));
            while (!lifetime.IsCancellationRequested)
            {
                await connection;
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                requestDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    var clientPid = WindowsBrokerPipeFactory.ClientProcessId(pipe.SafePipeHandle);
                    if (clientPid != registration.OwnerProcessId || owner.HasExited ||
                        owner.StartTime.ToUniversalTime() != registration.OwnerProcessStartUtc)
                        throw new UnauthorizedAccessException("Only the registered non-elevated control proxy may use this broker.");
                    var request = await WindowsCollectorProtocol.ReadAsync<WindowsCollectorRequest>(pipe, requestDeadline.Token);
                    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Credential), Encoding.UTF8.GetBytes(registration.ControlKey)))
                        throw new UnauthorizedAccessException("Invalid private collector authentication.");
                    if (request.RegistrationGeneration != registration.Generation ||
                        request.BrokerGeneration != registration.Grant.AttemptId || !WindowsGrantValidation.IsCurrent(registration))
                        throw new UnauthorizedAccessException("Stale profiler approval generation.");
                    WindowsCollectorReply response = request.Operation switch
                    {
                        WindowsCollectorOperation.Capabilities => new(Capability: await captures.Capabilities(requestDeadline.Token)),
                        WindowsCollectorOperation.Start when request.Start is not null => new(Capture: await captures.Start(request.Start, requestDeadline.Token)),
                        WindowsCollectorOperation.Get => new(Capture: await captures.Get(request.Id, requestDeadline.Token)),
                        WindowsCollectorOperation.Find => new(Capture: await captures.Find(request.Id, requestDeadline.Token)),
                        WindowsCollectorOperation.Stop => new(Capture: await captures.Stop(request.Id, request.Cancelled, requestDeadline.Token)),
                        WindowsCollectorOperation.Shutdown => new(),
                        _ => throw new ArgumentException("Unsupported collector operation.")
                    };
                    await WindowsCollectorProtocol.WriteAsync(pipe, response with
                    {
                        RegistrationGeneration = registration.Generation, BrokerGeneration = registration.Grant.AttemptId
                    }, requestDeadline.Token);
                    if (request.Operation == WindowsCollectorOperation.Shutdown) lifetime.Cancel();
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or
                    UnauthorizedAccessException or Win32Exception or JsonException)
                {
                    logger.LogWarning("Windows collector control rejected: {Reason}", exception.Message);
                    if (pipe.IsConnected)
                    {
                        try
                        {
                            await WindowsCollectorProtocol.WriteAsync(pipe, new WindowsCollectorReply(Error: exception.Message,
                                RegistrationGeneration: registration.Generation, BrokerGeneration: registration.Grant.AttemptId), requestDeadline.Token);
                        }
                        catch (Exception delivery) when (delivery is IOException or OperationCanceledException)
                        {
                            logger.LogWarning("Collector error response could not be delivered to the disconnected/timed-out client: {Reason}", delivery.Message);
                        }
                    }
                }
                catch (OperationCanceledException) when (requestDeadline.IsCancellationRequested)
                {
                    if (!lifetime.IsCancellationRequested) logger.LogWarning("Private collector request exceeded its deadline.");
                }
                if (pipe.IsConnected) pipe.Disconnect();
                if (!lifetime.IsCancellationRequested) connection = pipe.WaitForConnectionAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var failure = WindowsBrokerStartup.Failure(new(registration.Generation, registration.Grant.AttemptId,
                self.Id, self.StartTime.ToUniversalTime()), startupStage, exception);
            logger.LogError(exception, "Windows collector could not start/serve its verified control pipe: {Stage}", startupStage);
            try
            {
                using var reportDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var response = await authority.PostAsJsonAsync(
                    $"windows/approvals/{registration.Grant.OperationId}/startup-failure", failure, reportDeadline.Token);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception reporting) when (reporting is HttpRequestException or OperationCanceledException)
            {
                logger.LogError(reporting, "Exact collector startup failure could not be delivered to the current approval authority: {Failure}",
                    WindowsBrokerStartup.Describe(failure));
            }
            return 1;
        }
        finally
        {
            lifetime.Cancel();
            if (grantMonitor is not null) await grantMonitor;
        }
        return 0;

        async Task MonitorGrant()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(lifetime.Token))
                {
                    if (owner.HasExited || owner.StartTime.ToUniversalTime() != registration.OwnerProcessStartUtc ||
                        !WindowsGrantValidation.IsCurrent(registration))
                    {
                        logger.LogInformation("Profiler authorization was revoked/expired or its control process ended; stopping only this owned broker.");
                        lifetime.Cancel();
                        return;
                    }
                    await ConfirmActivation(lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or
                HttpRequestException || exception is OperationCanceledException && !lifetime.IsCancellationRequested)
            {
                logger.LogError(exception, "Profiler authorization could not be verified; broker is stopping");
                lifetime.Cancel();
            }
        }

        async Task ConfirmActivation(CancellationToken token)
        {
            using var response = await authority.PostAsJsonAsync($"windows/approvals/{registration.Grant.OperationId}/activate",
                new WindowsProfilerActivation(registration.Generation, registration.Grant.AttemptId, self.Id,
                    self.StartTime.ToUniversalTime()), token);
            response.EnsureSuccessStatusCode();
            var lease = await response.Content.ReadFromJsonAsync<WindowsProfilerActivationLease>(token);
            if (lease is not { Allowed: true } || lease.ExpiresUtc <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException(lease?.Error ?? "Profiler activation authority is unavailable.");
        }
    }
}
