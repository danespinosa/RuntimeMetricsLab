using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Coordination;

namespace RuntimeMetricsLab.ApiService.Endpoints;

/// <summary>
/// Catalog/start/status/cancel surface for the experiment coordinator, mounted under
/// <c>/api/experiments</c>. The primary routes are keyed by <see cref="ExperimentId"/> (matching
/// the coordinator's one-active-run-per-experiment model); a small set of <c>/runs/{runId}</c>
/// aliases is also exposed for callers that only have a run id at hand.
/// </summary>
public static class ExperimentEndpoints
{
    public static IEndpointRouteBuilder MapExperimentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/experiments").WithTags("Experiments");

        group.MapGet("/", (ExperimentCoordinator coordinator) =>
            Results.Ok(coordinator.GetCatalog()))
            .WithName("GetExperimentCatalog");

        group.MapGet("/{experimentId}/status", (ExperimentId experimentId, ExperimentCoordinator coordinator) =>
            Results.Ok(coordinator.GetStatus(experimentId)))
            .WithName("GetExperimentStatus");

        group.MapPost("/{experimentId}/runs", (ExperimentId experimentId, StartExperimentRunRequest? request, ExperimentCoordinator coordinator) =>
            Results.Ok(coordinator.Start(experimentId, (request ?? new StartExperimentRunRequest(null, null, null, null)).ToDomainRequest())))
            .WithName("StartExperiment");

        group.MapPost("/{experimentId}/cancel", (ExperimentId experimentId, ExperimentCoordinator coordinator) =>
            Results.Ok(coordinator.Cancel(experimentId)))
            .WithName("CancelExperiment");

        // Run-id-only convenience aliases: resolve the owning experiment first, then delegate.
        group.MapGet("/runs/{runId:guid}", (Guid runId, ExperimentCoordinator coordinator) =>
            coordinator.TryGetRun(runId, out var snapshot)
                ? Results.Ok(snapshot)
                : Results.Problem(title: "Run not found", detail: $"No run with id '{runId}' is known.", statusCode: StatusCodes.Status404NotFound))
            .WithName("GetExperimentRunById");

        group.MapDelete("/runs/{runId:guid}", (Guid runId, ExperimentCoordinator coordinator) =>
        {
            if (!coordinator.TryGetRun(runId, out var snapshot))
            {
                return Results.Problem(title: "Run not found", detail: $"No run with id '{runId}' is known.", statusCode: StatusCodes.Status404NotFound);
            }

            var current = coordinator.GetStatus(snapshot.ExperimentId);
            return current.RunId == runId
                ? Results.Ok(coordinator.Cancel(snapshot.ExperimentId))
                : Results.Problem(
                    title: "Run is no longer active",
                    detail: $"Run '{runId}' is historical and cannot be cancelled.",
                    statusCode: StatusCodes.Status409Conflict);
        })
            .WithName("CancelExperimentRunById");

        return app;
    }
}
