using RuntimeMetricsLab.Web.Api;
using RuntimeMetricsLab.Web.Components;
using RuntimeMetricsLab.Web.Services;
using RuntimeMetricsLab.Experiments.Diagnostics;
using RuntimeMetricsLab.Contracts.Attribution;

var builder = WebApplication.CreateBuilder(args);
var mode = AttributionModes.Parse(builder.Configuration["RuntimeMetricsLab:Mode"]);
var attributionMode = mode != AttributionMode.Standard;

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOutputCache();
builder.Services.AddRuntimeMetricsMonitoring();

builder.Services.AddHttpClient<ExperimentsApiClient>(client =>
    {
        // This URL uses "https+http://" to indicate HTTPS is preferred over HTTP.
        // Learn more about service discovery scheme resolution at https://aka.ms/dotnet/sdschemes.
        client.BaseAddress = new("https+http://apiservice");
        client.Timeout = TimeSpan.FromSeconds(15);
    });

// Scoped so each Blazor Server circuit (browser tab/session) gets its own in-memory run history.
builder.Services.AddScoped<RunTracker>();
if (attributionMode)
{
    builder.Services.AddSingleton<AttributionRuntime>();
    builder.Services.AddSingleton<AttributionJobs>();
    builder.Services.AddHostedService(services => services.GetRequiredService<AttributionJobs>());
}

var app = builder.Build();

if (mode == AttributionMode.Windows)
{
    app.Use(async (context, next) =>
    {
        context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self'";
        context.Response.Headers.XFrameOptions = "SAMEORIGIN";
        var interaction = !HttpMethods.IsGet(context.Request.Method) || context.Request.Path.StartsWithSegments("/_blazor");
        if (!WindowsUiOriginGuard.Allows(context.Request.Host.Host, context.Request.Scheme,
            context.Request.Host.Value, context.Request.Headers.Origin, interaction ? context.Request.Headers["Sec-Fetch-Site"].ToString() : null))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Windows profiler consent requires this loopback application's same-origin UI.");
            return;
        }
        await next(context);
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (!attributionMode)
    app.UseHttpsRedirection();

app.UseAntiforgery();

app.UseOutputCache();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();
if (attributionMode)
    app.MapAttributionJobEndpoints();

app.Run();
