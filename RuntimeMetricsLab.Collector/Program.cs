using System.Security.Cryptography;
using System.Text;
using RuntimeMetricsLab.Collector;
using RuntimeMetricsLab.Contracts.Attribution;
using RuntimeMetricsLab.Collector.Windows;

if (args.Contains("--windows-broker", StringComparer.Ordinal))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The PerfView launcher requires Windows.");
    Environment.ExitCode = await WindowsCollectorBroker.Run(args);
    return;
}
var builder = WebApplication.CreateBuilder(args);
var mode = AttributionModes.Parse(builder.Configuration["RuntimeMetricsLab:Mode"]);
if (mode == AttributionMode.Windows)
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows collector control requires Windows.");
    builder.Services.AddSingleton<IWindowsElevationLauncher, WindowsElevationLauncher>();
    builder.Services.AddSingleton<WindowsCollectorProxy>();
    builder.Services.AddSingleton<ICollectorControl>(services => OperatingSystem.IsWindows()
        ? services.GetRequiredService<WindowsCollectorProxy>() : throw new PlatformNotSupportedException());
    builder.Services.AddSingleton<IWindowsApprovalControl>(services => OperatingSystem.IsWindows()
        ? services.GetRequiredService<WindowsCollectorProxy>() : throw new PlatformNotSupportedException());
}
else
{
    CollectorBoundary.Verify();
    builder.Services.AddSingleton<CaptureManager>();
    builder.Services.AddSingleton<ICollectorControl, LinuxCollectorControl>();
}
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow);
var key = builder.Configuration["Attribution:CollectorKey"];
if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
    throw new InvalidOperationException("A per-lab collector credential of at least 32 characters is required.");
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Path != "/health" &&
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers["X-Lab-Key"].ToString()),
            Encoding.UTF8.GetBytes(key)))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { error = "Private collector authentication required." });
        return;
    }
    try { await next(context); }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or
        TimeoutException or UnauthorizedAccessException || exception is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogWarning(exception, "Collector control request rejected");
        context.Response.StatusCode = exception is BadHttpRequestException badRequest ? badRequest.StatusCode :
            exception is UnauthorizedAccessException ? 401 :
            exception is TimeoutException or OperationCanceledException ? 503 : exception is ArgumentException ? 400 : 409;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message });
    }
});
app.MapGet("/health", () => Results.Ok());
app.MapGet("/capabilities", (ICollectorControl captures, CancellationToken token) => captures.Capabilities(token));
app.MapPost("/captures", (CollectorStartRequest request, ICollectorControl captures, CancellationToken token) => captures.Start(request, token));
app.MapGet("/captures/by-operation/{id:guid}", async (Guid id, ICollectorControl captures, CancellationToken token) =>
    await captures.Find(id, token) is { } capture ? Results.Ok(capture) : Results.NotFound());
app.MapGet("/captures/{id:guid}", (Guid id, ICollectorControl captures, CancellationToken token) => captures.Get(id, token));
app.MapPost("/captures/{id:guid}/stop", (Guid id, CollectorStopRequest request, ICollectorControl captures, CancellationToken token) => captures.Stop(id, request.Cancelled, token));
if (mode == AttributionMode.Windows)
{
    app.MapPost("/windows/approvals/{id:guid}/begin", (Guid id, IWindowsApprovalControl proxy, CancellationToken token) =>
        proxy.Approvals.Begin(id, token));
    app.MapGet("/windows/approvals/{id:guid}", (Guid id, IWindowsApprovalControl proxy, CancellationToken token) =>
        proxy.Approvals.Get(id, token));
    app.MapPost("/windows/approvals/{id:guid}/accept", (Guid id, WindowsApprovalDecision decision, IWindowsApprovalControl proxy, CancellationToken token) =>
        proxy.Approvals.Accept(id, decision.RegistrationGeneration, token));
    app.MapPost("/windows/approvals/{id:guid}/cancel", (Guid id, IWindowsApprovalControl proxy, CancellationToken token) =>
        proxy.Approvals.Cancel(id, token));
    app.MapPost("/windows/approvals/{id:guid}/activate", (Guid id, WindowsProfilerActivation activation, IWindowsApprovalControl proxy, CancellationToken token) =>
        proxy.Activate(id, activation, token));
    app.MapPost("/windows/approvals/{id:guid}/startup-failure", (Guid id, WindowsBrokerStartupFailure failure, IWindowsApprovalControl proxy, CancellationToken token) =>
        proxy.ReportStartupFailure(id, failure, token));
}
app.Run();
