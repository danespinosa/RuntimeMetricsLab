using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting;
using RuntimeMetricsLab.Contracts.Attribution;

internal static class WindowsAttributionTopology
{
    public static void Add(IDistributedApplicationBuilder builder)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native Windows attribution must be launched on Windows.");
        var labId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.Environment.ContentRootPath)))[..10].ToLowerInvariant();
        var targetSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var collectorSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var targetKey = builder.AddParameter("attribution-target-key", () => targetSecret, secret: true);
        var collectorKey = builder.AddParameter("attribution-collector-key", () => collectorSecret, secret: true);

        var target = builder.AddProject<Projects.RuntimeMetricsLab_ApiService>("server-windows")
            .WithEnvironment("RuntimeMetricsLab__Mode", "attribution-windows")
            .WithEnvironment("Attribution__TargetKey", targetKey)
            .WithEnvironment("RUNTIME_METRICS_LAB_ROLE", "server")
            .WithEnvironment("RUNTIME_METRICS_LAB_PLATFORM", "windows-process")
            .WithHttpHealthCheck("/health");

        var collector = builder.AddProject<Projects.RuntimeMetricsLab_Collector>("attribution-windows-control")
            .WithHttpEndpoint(name: "http")
            .WithEnvironment("RuntimeMetricsLab__Mode", "attribution-windows")
            .WithEnvironment("Attribution__LabId", labId)
            .WithEnvironment("Attribution__CollectorKey", collectorKey)
            .WithEnvironment("Attribution__TargetKey", targetKey)
            .WithEnvironment("Attribution__TargetUrl", target.GetEndpoint("http"))
            .WithHttpHealthCheck("/health")
            .WaitFor(target);

        collector.WithEnvironment("Attribution__ControlUrl", collector.GetEndpoint("http"));

        builder.AddProject<Projects.RuntimeMetricsLab_Web>("client-windows")
            .WithEnvironment("RuntimeMetricsLab__Mode", "attribution-windows")
            .WithEnvironment("Attribution__LabId", labId)
            .WithEnvironment("Attribution__DataRoot", AttributionPaths.WindowsDataRoot(labId))
            .WithEnvironment("Attribution__CapturesRoot", AttributionPaths.WindowsCaptureRoot(labId))
            .WithEnvironment("Attribution__TargetKey", targetKey)
            .WithEnvironment("Attribution__CollectorKey", collectorKey)
            .WithEnvironment("Attribution__TargetUrl", target.GetEndpoint("http"))
            .WithEnvironment("Attribution__CollectorUrl", collector.GetEndpoint("http"))
            .WithEnvironment("RUNTIME_METRICS_LAB_ROLE", "client")
            .WithEnvironment("RUNTIME_METRICS_LAB_PLATFORM", "windows-process")
            .WithReference(target, "apiservice")
            .WithHttpHealthCheck("/health")
            .WithExternalHttpEndpoints()
            .WaitFor(target)
            .WaitFor(collector);
    }
}
