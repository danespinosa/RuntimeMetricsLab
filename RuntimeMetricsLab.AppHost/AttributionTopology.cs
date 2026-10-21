using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

internal static class AttributionTopology
{
    public static void Add(IDistributedApplicationBuilder builder)
    {
        var labId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.Environment.ContentRootPath)))[..10].ToLowerInvariant();
        var targetKeyValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var collectorKeyValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var targetKey = builder.AddParameter("attribution-target-key", () => targetKeyValue, secret: true);
        var collectorKey = builder.AddParameter("attribution-collector-key", () => collectorKeyValue, secret: true);
        var targetName = $"rml-target-{labId}";
        var diagnostics = $"rml-diagnostics-{labId}";
        var captures = $"rml-captures-{labId}";
        var data = $"rml-data-{labId}";

        var server = builder.AddDockerfile("server-linux", "..", "Attribution.Dockerfile")
            .WithContainerName(targetName)
            .WithArgs("/app/server/RuntimeMetricsLab.ApiService.dll")
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .WithHttpHealthCheck("/health")
            .WithVolume(diagnostics, "/tmp")
            .WithContainerRuntimeArgs("--user=1654:1654", "--cap-drop=ALL", "--security-opt=no-new-privileges",
                "--workdir=/app/server", $"--label=runtimemetricslab.lab={labId}")
            .WithEnvironment("Attribution__TargetKey", targetKey)
            .WithEnvironment("RUNTIME_METRICS_LAB_ROLE", "server")
            .WithEnvironment("RUNTIME_METRICS_LAB_PLATFORM", "linux-container")
            .WithOtlpExporter();

        var collector = builder.AddDockerfile("attribution-collector", "..", "Attribution.Dockerfile")
            .WithCertificateTrustScope(CertificateTrustScope.None)
            .WithArgs("/app/collector/RuntimeMetricsLab.Collector.dll")
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .WithHttpHealthCheck("/health")
            .WithVolume(diagnostics, "/tmp")
            .WithVolume(captures, "/captures")
            // These are daemon-side Linux paths, not Windows bind paths resolved by the AppHost.
            .WithContainerRuntimeArgs($"--pid=container:{targetName}", "--cap-drop=ALL", "--cap-add=PERFMON",
                "--cap-add=SYS_PTRACE", "--cap-add=DAC_OVERRIDE", "--cap-add=SYS_ADMIN", "--cap-add=SETPCAP",
                "--security-opt=no-new-privileges", "--entrypoint=/app/collector/bootstrap.sh",
                "--mount=type=bind,source=/sys/kernel/tracing,target=/sys/kernel/tracing,bind-propagation=rprivate",
                "--mount=type=bind,source=/sys/kernel/tracing/user_events_data,target=/sys/kernel/tracing/user_events_data,bind-propagation=rprivate",
                "--workdir=/app/collector", $"--label=runtimemetricslab.lab={labId}")
            .WithEnvironment("Attribution__CollectorKey", collectorKey)
            .WaitFor(server);

        builder.AddDockerfile("client-linux", "..", "Attribution.Dockerfile")
            .WithArgs("/app/web/RuntimeMetricsLab.Web.dll")
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .WithExternalHttpEndpoints()
            .WithHttpHealthCheck("/health")
            .WithVolume(data, "/data")
            .WithVolume(captures, "/captures", isReadOnly: true)
            .WithContainerRuntimeArgs("--user=1654:1654", "--cap-drop=ALL", "--security-opt=no-new-privileges",
                "--workdir=/app/web", $"--label=runtimemetricslab.lab={labId}")
            .WithEnvironment("Attribution__TargetKey", targetKey)
            .WithEnvironment("Attribution__CollectorKey", collectorKey)
            .WithEnvironment("Attribution__TargetUrl", server.GetEndpoint("http"))
            .WithEnvironment("Attribution__CollectorUrl", collector.GetEndpoint("http"))
            .WithEnvironment("services__apiservice__http__0", server.GetEndpoint("http"))
            .WithEnvironment("RUNTIME_METRICS_LAB_ROLE", "client")
            .WithEnvironment("RUNTIME_METRICS_LAB_PLATFORM", "linux-container")
            .WithOtlpExporter()
            .WaitFor(server)
            .WaitFor(collector);
    }
}
