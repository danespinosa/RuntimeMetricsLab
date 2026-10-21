#pragma warning disable ASPIRECERTIFICATES001 // HTTPS certificate configuration APIs are experimental.

var builder = DistributedApplication.CreateBuilder(args);
var mode = builder.Configuration["RuntimeMetricsLab:Mode"] ?? "standard";
if (mode == "attribution")
{
    AttributionTopology.Add(builder);
    builder.Build().Run();
    return;
}
if (mode == "attribution-windows")
{
    WindowsAttributionTopology.Add(builder);
    builder.Build().Run();
    return;
}
if (mode != "standard")
    throw new InvalidOperationException("RuntimeMetricsLab:Mode must be standard, attribution, or attribution-windows.");

// RuntimeMetricsLab.Web's WeatherApiClient hard-codes its HttpClient BaseAddress to
// "https+http://apiservice" (see WeatherApiClient.cs), so every client resource must inject its
// server's service-discovery information under the "apiservice" name regardless of the server
// resource's own Aspire resource name (server-windows / server-linux).
const string ApiServiceDiscoveryName = "apiservice";

// ---------------------------------------------------------------------------
// Windows-process resources: run the existing project code directly with `dotnet`,
// using each project's own launch profile ports (ApiService: 5352/7322, Web: 5211/7274).
// ---------------------------------------------------------------------------

var serverWindows = builder.AddProject<Projects.RuntimeMetricsLab_ApiService>("server-windows")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("RUNTIME_METRICS_LAB_ROLE", "server")
    .WithEnvironment("RUNTIME_METRICS_LAB_PLATFORM", "windows-process");

var clientWindows = builder.AddProject<Projects.RuntimeMetricsLab_Web>("client-windows")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithEnvironment("RUNTIME_METRICS_LAB_ROLE", "client")
    .WithEnvironment("RUNTIME_METRICS_LAB_PLATFORM", "windows-process")
    .WithReference(serverWindows, ApiServiceDiscoveryName)
    .WaitFor(serverWindows);

// ---------------------------------------------------------------------------
// Linux-container resources: same code, built into Linux containers from the multi-stage
// Dockerfiles under RuntimeMetricsLab.ApiService/Dockerfile and RuntimeMetricsLab.Web/Dockerfile.
// The build context is the repository root (one level above the AppHost project) so the
// Dockerfiles can COPY the shared ServiceDefaults/Contracts/Experiments projects.
// Runs via the Docker CLI backed by the WSL2 Docker Engine (DOCKER_HOST=tcp://127.0.0.1:2375).
// ---------------------------------------------------------------------------

var serverLinux = builder.AddDockerfile("server-linux", "..", "RuntimeMetricsLab.ApiService/Dockerfile")
    .WithHttpEndpoint(port: 5354, targetPort: 8080, name: "http")
    .WithHttpsEndpoint(port: 7324, targetPort: 8443, name: "https")
    .WithHttpsCertificateConfiguration(ctx =>
    {
        ctx.EnvironmentVariables["ASPNETCORE_Kestrel__Certificates__Default__Path"] = ctx.PfxPath;
        if (ctx.Password is not null)
        {
            ctx.EnvironmentVariables["ASPNETCORE_Kestrel__Certificates__Default__Password"] = ctx.Password;
        }

        return Task.CompletedTask;
    })
    .WithHttpsDeveloperCertificate()
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("ASPNETCORE_HTTP_PORTS", "8080")
    .WithEnvironment("ASPNETCORE_HTTPS_PORTS", "8443")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("RUNTIME_METRICS_LAB_ROLE", "server")
    .WithEnvironment("RUNTIME_METRICS_LAB_PLATFORM", "linux-container")
    // AddDockerfile container resources, unlike AddProject resources, do not automatically receive
    // OTEL_EXPORTER_OTLP_ENDPOINT/OTEL_SERVICE_NAME/OTEL_RESOURCE_ATTRIBUTES from the AppHost, so
    // without this the dashboard never sees traces/logs/metrics from server-linux.
    .WithOtlpExporter();

var clientLinux = builder.AddDockerfile("client-linux", "..", "RuntimeMetricsLab.Web/Dockerfile")
    .WithHttpEndpoint(port: 5213, targetPort: 8080, name: "http")
    .WithHttpsEndpoint(port: 7276, targetPort: 8443, name: "https")
    .WithExternalHttpEndpoints()
    .WithHttpsCertificateConfiguration(ctx =>
    {
        ctx.EnvironmentVariables["ASPNETCORE_Kestrel__Certificates__Default__Path"] = ctx.PfxPath;
        if (ctx.Password is not null)
        {
            ctx.EnvironmentVariables["ASPNETCORE_Kestrel__Certificates__Default__Password"] = ctx.Password;
        }

        return Task.CompletedTask;
    })
    .WithHttpsDeveloperCertificate()
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("ASPNETCORE_HTTP_PORTS", "8080")
    .WithEnvironment("ASPNETCORE_HTTPS_PORTS", "8443")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("RUNTIME_METRICS_LAB_ROLE", "client")
    .WithEnvironment("RUNTIME_METRICS_LAB_PLATFORM", "linux-container")
    // See the matching comment on server-linux above: container resources need WithOtlpExporter()
    // explicitly so client-linux's traces/logs/metrics reach the dashboard too.
    .WithOtlpExporter()
    // ContainerResource (returned by AddDockerfile) does not implement IResourceWithServiceDiscovery,
    // so the named WithReference(source, name) convenience overload used above for the project
    // resources doesn't apply here. Inject the same ".NET service discovery" environment variable
    // shape by hand ("services__{name}__{scheme}__{index}") using the "apiservice" name so
    // client-linux resolves server-linux the same way client-windows resolves server-windows.
    .WithEnvironment($"services__{ApiServiceDiscoveryName}__http__0", serverLinux.GetEndpoint("http"))
    .WithEnvironment($"services__{ApiServiceDiscoveryName}__https__0", serverLinux.GetEndpoint("https"))
    .WaitFor(serverLinux);

builder.Build().Run();

#pragma warning restore ASPIRECERTIFICATES001
