# RuntimeMetricsLab

A .NET 10 / .NET Aspire sandbox for exploring OpenTelemetry runtime metrics — GC,
thread pool, ASP.NET Core, `HttpClient`, and custom `System.Diagnostics.Metrics`
instruments defined in `RuntimeMetricsLab.Experiments` — **across two platforms**.
The AppHost now runs each app (server and client) as both a native Windows process
and a Linux container, so the same experiment can be compared Windows-process vs.
Linux-container side by side. The Aspire AppHost orchestrates all four resources
locally and the Aspire Dashboard is the primary tool for inspecting the metrics,
traces, and logs produced.

> **Status:** platform orchestration for the four app resources (`server-windows`,
> `client-windows`, `server-linux`, `client-linux`) is implemented; individual
> experiment content in `RuntimeMetricsLab.Experiments` continues to grow. See
> [`docs/roadmap.md`](docs/roadmap.md) for where this is headed and
> [`docs/adding-an-experiment.md`](docs/adding-an-experiment.md) for how to contribute one.

## Solution layout

| Project | Purpose |
|---|---|
| `RuntimeMetricsLab.AppHost` | Aspire orchestrator. Declares and wires the distributed application's resources. |
| `RuntimeMetricsLab.ServiceDefaults` | Shared OpenTelemetry / health-check / resilience / service-discovery wiring, referenced by every runnable service. |
| `RuntimeMetricsLab.ApiService` | ASP.NET Core backend. Built and run **twice** by the AppHost: as a native Windows process (`server-windows`) and as a Linux container from its own `Dockerfile` (`server-linux`). |
| `RuntimeMetricsLab.Web` | Blazor Web App frontend, **Interactive Server** render mode. Also built and run twice: `client-windows` (native process) and `client-linux` (Linux container from its own `Dockerfile`). |
| `RuntimeMetricsLab.Contracts` | Shared DTOs/interfaces between API and experiments. |
| `RuntimeMetricsLab.Experiments` | Home for custom `Meter`/instrument code, registered as an OTel meter + trace source in `ServiceDefaults`. |
| `RuntimeMetricsLab.Experiments.Tests` | xUnit tests for the experiments project. |

## Prerequisites

Verified working versions as of this writing (see [Recorded versions](#recorded-versions--drift) below — **re-check these yourself**, they drift):

- **.NET SDK 10.0.302** or later (`dotnet --version`). Projects target `net10.0`.
- **Aspire CLI 13.4.6** (`aspire --version`) — the AppHost project uses
  `Sdk="Aspire.AppHost.Sdk/13.4.6"` in `RuntimeMetricsLab.AppHost.csproj`. Install/update
  with the `aspire` CLI, not Visual Studio workloads alone.
- A terminal/IDE that can run the AppHost project (Visual Studio 2022 17.8+, VS Code +
  C# Dev Kit, or plain `dotnet`/`aspire` CLI).
- **Docker is required now.** Two of the four app resources — `server-linux` and
  `client-linux` — are declared with `builder.AddDockerfile(...)` in `AppHost.cs` and
  are built/run as Linux containers on every `aspire run`. There is no way to start
  the full topology without a working container runtime.
  - Use the **Docker CLI backed by the WSL2-hosted engine**. Set
    `DOCKER_HOST=tcp://127.0.0.1:2375` (the IPv4 literal — `localhost` resolves to
    IPv6 `::1`, which the daemon does not listen on) and ensure the WSL2 Ubuntu
    `dockerd` is running (`wsl -d Ubuntu -u root systemctl start docker` if not).
  - **Never install or use Docker Desktop** — it is intentionally not part of this
    environment; diagnose the WSL2 engine instead if `docker`/the AppHost can't reach it.
  - Confirm connectivity before running the AppHost: `docker version` and
    `docker info` should both succeed with `DOCKER_HOST` set as above.

## Starting the app

From the repo root:

```powershell
aspire run
```

or, without the Aspire CLI:

```powershell
dotnet run --project RuntimeMetricsLab.AppHost
```

Or open `RuntimeMetricsLab.sln` and F5 the `RuntimeMetricsLab.AppHost` project. The
console output prints the Aspire Dashboard URL (typically
`https://localhost:17xxx`, with a login token on first run) — open it in a browser.

## Topology: four app resources, two platforms each

`AppHost.cs` declares **four application resources**, each server/client pair built
from the *same* project code but run on two different platforms so runtime behavior
can be compared side by side:

```csharp
const string ApiServiceDiscoveryName = "apiservice";

// Windows-process resources: run the existing project code directly with `dotnet`.
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

// Linux-container resources: same code, built into Linux containers from the
// multi-stage Dockerfiles under RuntimeMetricsLab.ApiService/Dockerfile and
// RuntimeMetricsLab.Web/Dockerfile. Runs via the Docker CLI backed by the
// WSL2 Docker Engine.
var serverLinux = builder.AddDockerfile("server-linux", "..", "RuntimeMetricsLab.ApiService/Dockerfile")
    .WithHttpEndpoint(port: 5354, targetPort: 8080, name: "http")
    .WithHttpsEndpoint(port: 7324, targetPort: 8443, name: "https")
    // ...certificate + env config...
    .WithHttpHealthCheck("/health")
    .WithEnvironment("RUNTIME_METRICS_LAB_ROLE", "server")
    .WithEnvironment("RUNTIME_METRICS_LAB_PLATFORM", "linux-container");

var clientLinux = builder.AddDockerfile("client-linux", "..", "RuntimeMetricsLab.Web/Dockerfile")
    .WithHttpEndpoint(port: 5213, targetPort: 8080, name: "http")
    .WithHttpsEndpoint(port: 7276, targetPort: 8443, name: "https")
    .WithExternalHttpEndpoints()
    // ...certificate + env config...
    .WithHttpHealthCheck("/health")
    .WithEnvironment("RUNTIME_METRICS_LAB_ROLE", "client")
    .WithEnvironment("RUNTIME_METRICS_LAB_PLATFORM", "linux-container")
    .WithEnvironment($"services__{ApiServiceDiscoveryName}__http__0", $"{serverLinux.GetEndpoint("http")}")
    .WithEnvironment($"services__{ApiServiceDiscoveryName}__https__0", $"{serverLinux.GetEndpoint("https")}")
    .WaitFor(serverLinux);
```

The four resources you'll see in the Dashboard's resource graph:

| Resource | Project | Platform | Ports (host) | Role |
|---|---|---|---|---|
| `server-windows` | `RuntimeMetricsLab.ApiService` | Native Windows process (`dotnet`) | 5352 / 7322 (launch profile) | Backend API |
| `client-windows` | `RuntimeMetricsLab.Web` | Native Windows process (`dotnet`) | 5211 / 7274 (launch profile) | Blazor frontend, calls `server-windows` |
| `server-linux` | `RuntimeMetricsLab.ApiService` | Linux container, built from `RuntimeMetricsLab.ApiService/Dockerfile` | 5354 / 7324 → container 8080 / 8443 | Backend API |
| `client-linux` | `RuntimeMetricsLab.Web` | Linux container, built from `RuntimeMetricsLab.Web/Dockerfile` | 5213 / 7276 → container 8080 / 8443 | Blazor frontend, calls `server-linux` |

Notes:

- Every resource is tagged with `RUNTIME_METRICS_LAB_ROLE` (`server`/`client`) and
  `RUNTIME_METRICS_LAB_PLATFORM` (`windows-process`/`linux-container`) environment
  variables, which `ServiceDefaults` copies onto the OTel resource as
  `runtime.metrics.lab.role` / `runtime.metrics.lab.platform` attributes — use these
  to filter/compare metrics across the four resources in the Dashboard or any OTel
  backend.
- Both clients resolve their server via the **same** service-discovery name,
  `"apiservice"` — `client-windows` gets it "for free" via `WithReference`, while
  `client-linux` (a container resource, not a project resource) gets the equivalent
  `services__apiservice__{scheme}__0` environment variables set by hand, since
  `AddDockerfile` doesn't implement `IResourceWithServiceDiscovery`.
- The AppHost itself and the Aspire Dashboard remain the two pieces of local-run
  infrastructure around these four resources, but they are not part of the
  application topology being measured — see [`docs/architecture.md`](docs/architecture.md)
  for the full diagram, the Blazor Interactive Server distinction, and the
  Windows-process vs. Linux-container caveats for the metrics you'll see from each pair.

## Dashboard workflow

1. Open the Dashboard URL printed at startup.
2. **Resources** tab — confirm all four resources (`server-windows`, `client-windows`,
   `server-linux`, `client-linux`) are `Running` and healthy. The two `-linux`
   resources take longer on first run since Docker has to build their images.
3. **Console logs** — per-resource stdout/stderr, useful for startup failures. For the
   `-linux` resources this includes the `docker build` output on first run.
4. **Structured logs** / **Traces** — correlated OTel logs and distributed traces
   across all four services; filter/compare by the `runtime.metrics.lab.role` and
   `runtime.metrics.lab.platform` resource attributes to isolate a platform pair.
5. **Metrics** tab — select a resource, then a **meter** (e.g. `System.Runtime`,
   `Microsoft.AspNetCore.Hosting`, `System.Net.Http`, `RuntimeMetricsLab.Experiments`),
   then an **instrument** to chart it live — see
   [`docs/metric-catalog.md`](docs/metric-catalog.md) for the mapping and why it must be
   verified against your running instance rather than trusted from documentation alone.
   Pick the same instrument on a `-windows` and `-linux` resource to compare platforms.
6. Stop the app (Ctrl+C in the AppHost console, or `aspire stop`) when done —
   the Dashboard and all four resources shut down together.

## Safety notes

- **Never commit secrets.** The AppHost uses `dotnet user-secrets`
  (`UserSecretsId` in its `.csproj`); keep OTLP endpoints, connection strings, and
  tokens out of `appsettings*.json` and out of git.
- **Health endpoints are dev-only by design** — `MapDefaultEndpoints()` only maps
  `/health` and `/alive` when `IsDevelopment()` is true (see
  `ServiceDefaults/Extensions.cs`); don't enable them in non-development environments
  without reviewing `https://aka.ms/aspire/healthchecks`.
- **Watch metric cardinality.** Any custom instrument added under
  `RuntimeMetricsLab.Experiments` must follow the low-cardinality tag guidance in
  [`docs/metric-catalog.md`](docs/metric-catalog.md) — unbounded tag values (user IDs,
  request paths with IDs, timestamps) will blow up the Dashboard and any real
  OTel backend.
- **Don't point `OTEL_EXPORTER_OTLP_ENDPOINT` at a production collector** while
  experimenting — `AddOpenTelemetryExporters()` in `ServiceDefaults` activates the OTLP
  exporter whenever that variable is set, with no environment guard.
- **No Docker Desktop, ever.** `server-linux`/`client-linux` require a working
  container runtime to even start — use the Docker CLI against the WSL2-hosted
  engine (`DOCKER_HOST=tcp://127.0.0.1:2375`, IPv4 literal). If the AppHost can't
  build/start the `-linux` resources, diagnose the WSL2 engine (`wsl -d Ubuntu -u root
  systemctl start docker`, or re-warm via the `WSL Docker Engine Keepalive` scheduled
  task) — never install Docker Desktop as a workaround.
- **Don't confuse the two `-linux` resources with production-grade containers.**
  `WithHttpsDeveloperCertificate()`/`WithHttpsCertificateConfiguration` inject a local
  dev HTTPS certificate into the container for convenience; these images are not
  hardened for anything beyond local experimentation.

## Recorded versions & drift

| Tool/package | Version at time of writing |
|---|---|
| .NET SDK | 10.0.302 |
| Aspire CLI / templates | 13.4.6 (stable channel, per `aspire.config.json`) |
| `Aspire.AppHost.Sdk` | 13.4.6 |
| `OpenTelemetry.Extensions.Hosting` / `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.15.3 |
| `OpenTelemetry.Instrumentation.AspNetCore` | 1.15.2 |
| `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime` | 1.15.1 |
| `Microsoft.Extensions.Http.Resilience`, `Microsoft.Extensions.ServiceDiscovery` | 10.6.0 |
| `Microsoft.AspNetCore.OpenApi` | 10.0.8 |

These are the versions NuGet resolved when the projects were generated. **.NET, Aspire,
and OpenTelemetry all ship frequently** — instrument names, meter names, units, and CLI
behavior can and do change between minor/patch releases. Before relying on any version
number or metric name in this documentation, re-verify with `dotnet --version`,
`aspire --version`, and by inspecting `**/*.csproj` / `obj/**/project.assets.json`
directly, and cross-check metrics against the live Dashboard rather than trusting a
doc snapshot.
