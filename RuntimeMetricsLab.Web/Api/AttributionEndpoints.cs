using System.IO.Compression;
using RuntimeMetricsLab.Contracts.Attribution;
using RuntimeMetricsLab.Web.Services;

namespace RuntimeMetricsLab.Web.Api;

public static class AttributionEndpoints
{
    private static readonly SemaphoreSlim ExportGate = new(1, 1);
    public static void MapAttributionJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/attribution");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
            {
                app.Logger.LogWarning(exception, "Attribution request rejected");
                if (context.HttpContext.Response.HasStarted) throw;
                return Results.Problem(exception.Message, statusCode: exception is ArgumentException ? 400 : 409);
            }
        });
        group.MapGet("/capabilities", (AttributionJobs jobs, CancellationToken token) => jobs.Capabilities(token));
        group.MapGet("/jobs", (AttributionJobs jobs) => jobs.History());
        group.MapPost("/jobs", (StartAttributionRequest request, AttributionJobs jobs) => jobs.Start(request));
        group.MapGet("/jobs/{id:guid}", (Guid id, AttributionJobs jobs) => jobs.Get(id));
        group.MapPost("/jobs/{id:guid}/cancel", (Guid id, AttributionJobs jobs) => jobs.Cancel(id));
        group.MapPost("/jobs/{id:guid}/reanalyze", (Guid id, AttributionJobs jobs) => jobs.Reanalyze(id));
        group.MapGet("/jobs/{id:guid}/report", (Guid id, AttributionJobs jobs) =>
            jobs.Report(id) is { } report ? Results.Json(report, AttributionJson.ReportOptions) : Results.NotFound());
        group.MapGet("/jobs/{id:guid}/counter-diagnostic", (Guid id, AttributionJobs jobs) =>
            jobs.DiagnosticReport(id) is { } report ? Results.Json(report) : Results.NotFound());
        group.MapGet("/jobs/{id:guid}/bundle", async (Guid id, AttributionJobs jobs, HttpContext context) =>
        {
            var job = jobs.Get(id);
            if (!job.IsTerminal || job.CaptureId is null || !Directory.Exists(jobs.Paths.Capture(job.CaptureId.Value)))
            {
                context.Response.StatusCode = 409;
                await context.Response.WriteAsJsonAsync(new { error = "No finalized local capture is available. Interrupted collector artifacts require collector recovery first." });
                return;
            }
            var captureRoot = jobs.Paths.Capture(job.CaptureId.Value);
            var format = jobs.Report(id) is { } saved
                ? AttributionModes.Provenance(saved.Collector).Format
                : jobs.Mode == AttributionMode.Windows ? AttributionTraceFormat.PerfViewEtw : AttributionTraceFormat.UniversalNetTraceV6;
            var artifacts = new[]
            {
                (jobs.DirectoryFor(id), "job.json"), (jobs.DirectoryFor(id), "bundle.json"),
                (jobs.DirectoryFor(id), "target.json"),
                (jobs.DirectoryFor(id), "partial-target.json"),
                (jobs.DirectoryFor(id), Path.GetFileName(jobs.LatestReportPath(id) ?? "report-v1.json")),
                (jobs.DirectoryFor(id), "analysis.log")
            }.Concat(AttributionArtifacts.CollectorFiles(format, includeRundown: true).Select(name => (captureRoot, name)))
                .Concat(jobs.Mode == AttributionMode.Windows
                    ? new[] { (captureRoot, AttributionArtifacts.WindowsRundown), (captureRoot, "collector-error.log") }
                    : Array.Empty<(string, string)>())
                .Select(item => (Path: Path.Combine(item.Item1, item.Item2), Name: item.Item2))
                .Distinct()
                .Where(item => File.Exists(item.Path)).ToArray();
            await ExportGate.WaitAsync(context.RequestAborted);
            var temporary = Path.Combine(jobs.Paths.ExportsRoot, $"{id:N}-{Guid.NewGuid():N}.zip");
            try
            {
                var bytes = artifacts.Sum(item => new FileInfo(item.Path).Length);
                if (new DriveInfo(jobs.Paths.DataRoot).AvailableFreeSpace < bytes + 64L * 1024 * 1024)
                    throw new IOException("Insufficient local space to finalize a bundle download; existing captures were preserved.");
                Directory.CreateDirectory(jobs.Paths.ExportsRoot);
                await using var bundle = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                // The pinned runtime's ZIP entry DisposeAsync still flushes synchronously.
                // Finalize on a bounded local file, never by enabling synchronous Kestrel IO.
                using (var zip = new ZipArchive(bundle, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var artifact in artifacts)
                    {
                        await using var input = File.OpenRead(artifact.Path);
                        using var output = zip.CreateEntry(artifact.Name, CompressionLevel.Fastest).Open();
                        await input.CopyToAsync(output, context.RequestAborted);
                    }
                }
                bundle.Position = 0;
                context.Response.ContentType = "application/zip";
                context.Response.ContentLength = bundle.Length;
                context.Response.Headers.ContentDisposition = $"attachment; filename=\"attribution-{id:N}.zip\"";
                await bundle.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                finally { ExportGate.Release(); }
            }
        });
    }
}
