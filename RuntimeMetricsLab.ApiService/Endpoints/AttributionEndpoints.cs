using System.Security.Cryptography;
using System.Text;
using RuntimeMetricsLab.Contracts.Attribution;
using RuntimeMetricsLab.Experiments.Attribution;
using RuntimeMetricsLab.Experiments.Diagnostics;

namespace RuntimeMetricsLab.ApiService.Endpoints;

public static class AttributionEndpoints
{
    public static void MapAttributionEndpoints(this WebApplication app)
    {
        var key = app.Configuration["Attribution:TargetKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            throw new InvalidOperationException("A private attribution target control credential of at least 32 characters is required.");
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/attribution") &&
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers["X-Lab-Key"].ToString()),
                    Encoding.UTF8.GetBytes(key)))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Private target control authentication required." });
                return;
            }
            if (context.Request.Path.StartsWithSegments("/api/experiments") || context.Request.Path.StartsWithSegments("/workloads"))
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = "Legacy workloads are disabled in attribution mode to protect the target-wide capture lease." });
                return;
            }
            await next(context);
        });
        var group = app.MapGroup("/api/attribution");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ArgumentException exception) { return Results.Problem(exception.Message, statusCode: 400); }
            catch (InvalidOperationException exception) { return Results.Problem(exception.Message, statusCode: 409); }
        });
        group.MapGet("/status", (AttributionTarget target) => target.Status());
        group.MapGet("/runtime", (RuntimeSampler sampler) => sampler.Sample());
        group.MapPost("/prepare", (PrepareTargetRequest request, AttributionTarget target) => target.Prepare(request));
        group.MapPost("/{id:guid}/run", (Guid id, AttributionTarget target) => target.Run(id));
        if (AttributionModes.Parse(app.Configuration["RuntimeMetricsLab:Mode"]) == AttributionMode.Windows)
            group.MapPost("/{id:guid}/counter-diagnostic", (Guid id, AttributionTarget target) => target.RunCounterDiagnostic(id));
        group.MapPost("/{id:guid}/cancel", (Guid id, AttributionTarget target) => target.Cancel(id));
        group.MapPost("/{id:guid}/release", async (Guid id, AttributionTarget target) =>
        {
            await target.ReleaseAsync(id);
            return Results.Ok();
        });
    }
}
