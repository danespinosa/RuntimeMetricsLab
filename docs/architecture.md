# Architecture

## Modes

`RuntimeMetricsLab:Mode` defaults to `standard`; `attribution` is the explicit
Linux-only opt-in and `attribution-windows` selects native Windows processes.
`AppHost.cs` selects the topology and rejects unknown mode names.
The existing Windows/Linux four-resource comparison is unchanged by default.

Native Windows replaces Linux namespaces/cgroup paths with typed process
identity/creation time, actual token owner, processor-group/CPU-set/job metadata
and local Windows storage. A non-elevated authenticated control proxy gates a
small, explicitly consented/UAC-elevated launcher over a restricted named
pipe. It invokes stock Microsoft PerfView rather than a custom ETW engine.
PerfView's private raw ETL contains system-wide kernel events; FocusProcess
limits non-kernel providers only. Offline analysis selects the fixed lab target.
This elevated end-to-end path remains permission blocked until a real approved
PerfView capture and calibration succeed; counters and allocation-only evidence must not
be mislabeled as full method CPU attribution. See [Windows architecture and
authorization](windows-attribution.md).

Windows profile jobs now have a separate consent/elevation readiness lifecycle.
The web job owns a server-circuit consent challenge, while the non-elevated proxy
owns the durable generation-bound approval and asynchronous fixed `runas` launch.
Approval/readiness waits do not spend the workload-duration timeout. Immutable
attempt files, revocation/deadline/heartbeat checks and short live activation
leases reject cancelled or late UAC starts. Readiness IO never holds the
approval-state lock. Linux profile jobs and standard mode do not enter this gate.

## Attribution topology

```text
Blazor client-linux (UID 1654; durable job service)
  |-- authenticated target control --> server-linux (UID 1654)
  |                                      named lessons + target-wide lease
  |                                      raw monotonic runtime samples + call counts
  |-- authenticated collector control --> attribution-collector
  |                                      target-only PID namespace + shared /tmp
  |                                      fixed OneCollect collect-linux command
  `-- separate analysis worker (UID 1654; cleared environment, no control credentials)
                                         typed TraceEvent adapter -> versioned report
```

`AttributionTopology.cs` and `Attribution.Dockerfile` wire the three containers.
The analysis worker is a separate process in the client container, never in the
measured server or privileged collector. Container CPU/memory quotas are unset.
The UI and target run without capabilities. The collector's fixed bootstrap
works around Docker 29 pre-start archive handling, makes its private parent
tracefs bind read-only, and irreversibly drops temporary `SYS_ADMIN`/`SETPCAP`
before the service opens a port. Runtime capabilities are exactly `PERFMON`,
`SYS_PTRACE`, and `DAC_OVERRIDE`, with no-new-privileges and only
`user_events_data` writable. Host mount flags and host security are unchanged.

There is no host PID namespace, Docker socket in any application container,
arbitrary command/PID/provider control endpoint, or machine-wide collection.
Per-start credentials are separate for target and collector; health endpoints
are non-sensitive, and published ports bind to loopback. Do not dump raw Aspire
environment JSON: it can expose credentials even for secret parameters.

### Capture and persistence

`AttributionJobs` is a singleton background service, independent of Blazor
circuits. It journals jobs atomically, honors idempotency keys, and admits one
capture/analysis at a time. Private control clients intentionally bypass generic
HTTP retry handlers for mutations. The target reserves one lease, warms methods,
waits for actual marker-provider enablement, measures bounded work between
nonce-bearing boundaries, and records recovery separately. All legacy workload
routes, including gRPC, are disabled in attribution mode.

The collector accepts only the fixed target alias, bounded duration and operation
identity; it generates capture IDs and paths. It signals only its owned tracer,
checks target start time/namespace, and atomically finalizes complete or explicitly
partial artifacts. A `D + 30` capture budget covers up to 20 seconds of attachment,
baseline, work and recovery; the maximum is 90 seconds. Early tracer completion
and deadline/size violations fail visibly.

Lab-scoped Docker named volumes are derived from the AppHost directory:

| Volume | Contents / access |
|---|---|
| `rml-diagnostics-<lab>` | Shared target/collector `/tmp`; no host diagnostic directory |
| `rml-captures-<lab>` | Collector-owned finalized trace/diagnostics; client/analysis read-only |
| `rml-data-<lab>` | Durable jobs, immutable counter bundles, versioned reports and derived indexes |

Capture bundles record schema, run/target identity, settings, runtime/build/cgroup
metadata, raw samples, invocation counts, tool versions and artifact hashes.
Reports use explicit covered CPU pools and independent allocation weights.
Exact-signature/unique-module evidence reconciles tiered-JIT identities; ambiguous
identities remain separate. Flat inclusive costs handle recursion without double
counting; call-path trees retain distinct callers.

Raw captures are immutable after finalization. Reanalysis adds a report revision.
Interrupted jobs are not silently rerun; incomplete artifacts are preserved.
Storage pressure stops new captures rather than deleting history. The worker
has bounded input/event/frame/tree expansion and a finite time/working-set
safeguard; a file-size bound is not claimed to be an equivalent memory cap.

See [the attribution walkthrough](attribution-lab.md) for calculations, evidence
quality (including unknown kernel loss), graphs and memory-ownership limitations.

## Standard-mode component diagram

```
                                  ┌───────────────────────────────┐
                                  │   RuntimeMetricsLab.AppHost    │
                                  │  (Aspire orchestrator / DCP)   │
                                  └───────────────┬─────────────────┘
                                                  │ starts & wires resources
                     ┌──────────────────┬─────────┴─────────┬──────────────────┐
                     │                  │                   │                  │
                     ▼                  ▼                   ▼                  ▼
          ┌────────────────────┐ ┌────────────────────┐ ┌────────────────────┐ ┌────────────────────┐
          │  server-windows     │ │  client-windows     │ │  server-linux       │ │  client-linux       │
          │  ApiService,        │◄┤  Web (Blazor,       │ │  ApiService,        │◄┤  Web (Blazor,       │
          │  native Windows     │ │  Interactive Server) │ │  Linux container    │ │  Linux container    │
          │  process            │ │  native Windows     │ │  from Dockerfile    │ │  from Dockerfile    │
          └─────────┬───────────┘ │  process            │ └─────────┬───────────┘ │  (Docker/WSL2)      │
                    │             └──────────┬───────────┘           │             └─────────┬──────────┘
                    │                        │                       │                       │
                    └────────────┬───────────┘                       └────────────┬──────────┘
                                 │  OTel logs/traces/metrics                       │
                                 ▼                                                ▼
                          ┌─────────────────────────────────────────────────────────┐
                          │                 Aspire Dashboard (UI + OTLP receiver)    │
                          └─────────────────────────────────────────────────────────┘
```

There are **four application resources**, forming two identical server/client pairs
run on two different platforms:

| Pair | Server resource | Client resource | Platform |
|---|---|---|---|
| Windows | `server-windows` | `client-windows` | Native Windows process, launched via `dotnet` by the AppHost |
| Linux | `server-linux` | `client-linux` | Linux container, built from a project-local multi-stage `Dockerfile`, run via the Docker CLI against the WSL2 Docker Engine |

Both pairs run the **same project code** (`RuntimeMetricsLab.ApiService` /
`RuntimeMetricsLab.Web`) — only the execution platform differs. This is the point of
the lab: the same experiment can be triggered on `server-windows` and `server-linux`
(or `client-windows`/`client-linux`) and the resulting metrics compared directly.
Each resource is stamped with `RUNTIME_METRICS_LAB_ROLE` (`server`/`client`) and
`RUNTIME_METRICS_LAB_PLATFORM` (`windows-process`/`linux-container`) environment
variables, surfaced by `ServiceDefaults` as `runtime.metrics.lab.role` /
`runtime.metrics.lab.platform` OTel resource attributes for exactly this comparison.

All four reference `RuntimeMetricsLab.ServiceDefaults`, which centralizes
OpenTelemetry, health checks, service discovery, and resilient `HttpClient`
configuration (`ServiceDefaults/Extensions.cs`), and all four reference
`RuntimeMetricsLab.Contracts` (shared types) and `RuntimeMetricsLab.Experiments`
(custom metric instruments, wired in as an OTel meter + trace source) — see
[`docs/roadmap.md`](roadmap.md).

## Project responsibilities

- **AppHost** (`AppHost.cs`) — the only place resources are declared. Declares
  `server-windows`/`client-windows` via `builder.AddProject<...>(...)` and
  `server-linux`/`client-linux` via `builder.AddDockerfile(...)`, wires each client to
  its server (`WithReference`/hand-rolled service-discovery env vars + `WaitFor`), and
  gives every resource an HTTP health-check probe (`WithHttpHealthCheck`). This is the
  source of truth for the topology described in the README.
- **ServiceDefaults** — cross-cutting concerns shared by all four resources:
  `ConfigureOpenTelemetry` (logging, a broad set of built-in ASP.NET Core/networking/
  gRPC meters plus the `RuntimeMetricsLab.Experiments` meter and trace source, OTLP
  exporter activation gated on `OTEL_EXPORTER_OTLP_ENDPOINT`), default health checks,
  service-discovery-aware `HttpClient` defaults with standard resilience, and the
  `runtime.metrics.lab.role`/`runtime.metrics.lab.platform` resource attributes
  derived from the AppHost-injected environment variables.
- **ApiService** — the backend, run as both `server-windows` and `server-linux`.
  This is where experiment endpoints (e.g. "trigger a GC-heavy workload") live.
- **Web** — the Blazor frontend, run as both `client-windows` and `client-linux`,
  calling its paired server through `ExperimentsApiClient` over `https+http://apiservice`
  (service-discovery scheme resolution — the discovery name `"apiservice"` is constant
  across both platform pairs even though the underlying resource names differ).
- **Contracts** — shared plain types flowing between `ApiService`, `Web`, and
  `Experiments`.
- **Experiments** — custom `System.Diagnostics.Metrics.Meter`/instrument code
  exercising specific runtime behaviors, registered into every resource's OTel
  pipeline. See [`docs/adding-an-experiment.md`](adding-an-experiment.md).

## Blazor Interactive Server: why it matters for metrics

`RuntimeMetricsLab.Web/Program.cs` configures:

```csharp
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
...
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
```

This is **Interactive Server** render mode — component code executes entirely on the
server, and the browser holds a persistent SignalR-style circuit back to the server
process for UI updates. Practically, this means, **for both `client-windows` and
`client-linux`**:

- **All Blazor component code runs in the client resource's own .NET process/container.**
  Any runtime metrics (GC, thread pool, allocations) attributable to rendering UI show
  up under `client-windows` or `client-linux` in the Dashboard — never in the browser,
  and never attributed to the paired server resource.
  There is no client-side .NET runtime and therefore **no WASM runtime meters** to
  observe — this project does not use Blazor WebAssembly or Auto render modes.
- **Circuits are a stateful resource per connected browser tab**, held by whichever
  client resource the browser connected to. Each active circuit consumes memory/threads
  on that resource for its lifetime. `ServiceDefaults` already wires up the
  `Microsoft.AspNetCore.Components.Server.Circuits` meter (see
  `BuiltInMeterNames` in `Extensions.cs`) — verify its exact instrument names/units
  against your running Dashboard rather than assuming from this doc (see
  [`docs/metric-catalog.md`](metric-catalog.md)). This is distinct from plain HTTP
  request metrics (`Microsoft.AspNetCore.Hosting`), which measure the initial page
  request only, not the long-lived circuit.
- Comparing circuit metrics between `client-windows` and `client-linux` under
  identical load is a natural first experiment for this project, since it isolates
  the platform variable while holding the render-mode/code path constant.

## Windows process vs. Linux container: now a real, live comparison

In default standard mode the AppHost runs both variants, so this section describes
the normal comparison topology rather than the opt-in attribution topology:

- `server-windows`/`client-windows` run as **native Windows processes** launched by
  the AppHost via `dotnet`. `server-linux`/`client-linux` run as **Linux containers**,
  built from `RuntimeMetricsLab.ApiService/Dockerfile` and
  `RuntimeMetricsLab.Web/Dockerfile` respectively (multi-stage builds:
  `mcr.microsoft.com/dotnet/sdk:10.0` to publish, `mcr.microsoft.com/dotnet/aspnet:10.0`
  to run), executed via the **Docker CLI backed by the WSL2 Docker Engine**.
- **CLR/runtime metrics (`System.Runtime` meter via `AddRuntimeInstrumentation`) are
  produced by `System.Diagnostics.Metrics`/EventCounters inside the CLR itself**, so
  the *instrument names* are the same across all four resources. Don't expect
  different metric *names* between the Windows and Linux pairs — expect different
  *values* under the same workload.
- **What differs is the environment the CLR observes.** `server-windows`/
  `client-windows` run with no cgroup, so container-aware dimensions (e.g. cgroup
  memory/CPU limits informing the GC's heap hard-limit behavior) won't be present or
  meaningful there — the CLR falls back to host-wide Windows job/OS limits. On
  `server-linux`/`client-linux`, the CLR runs under Linux cgroups inside the
  container, so container-aware GC behavior (and any cgroup memory/CPU limit you set
  on the container) actually applies — this pair is the representative one for any
  experiment specifically about containerized GC/resource-limit behavior.
- **Docker Desktop is not used and must not be installed.** The `-linux` resources
  require `DOCKER_HOST=tcp://127.0.0.1:2375` (IPv4 literal — `localhost` resolves to
  IPv6 and the daemon won't answer) pointing at the WSL2 Ubuntu `dockerd`. If
  `aspire run` fails to build/start `server-linux`/`client-linux`, diagnose the WSL2
  engine (`wsl -d Ubuntu -u root systemctl start docker`, or re-warm the
  `WSL Docker Engine Keepalive` scheduled task) — never fall back to Docker Desktop.
- Both Dockerfiles use a repo-root build context (`AddDockerfile("server-linux", "..",
  "RuntimeMetricsLab.ApiService/Dockerfile")`) so they can `COPY` the shared
  `ServiceDefaults`/`Contracts`/`Experiments` projects alongside the app project —
  keep new shared-project references in sync between the `.csproj` files and both
  Dockerfiles' `COPY` lists, or the container build will fail to restore.
- This repo does not target Windows-base-image containers (`nanoserver`/Server Core
  via `nerdctl`/`containerd`) — `server-windows`/`client-windows` are native Windows
  *processes*, not Windows containers. If a future variant specifically needs a
  Windows container, treat it as a distinct toolchain (elevated `nerdctl`,
  `--isolation hyperv`, `--platform windows/amd64`) separate from the Linux/Docker CLI
  path above, and document that decision here when it happens.

## Verification checklist

Architecture facts above (topology, render mode, meter names) were true as of the
state described in the README's version table. Before trusting this document for
anything load-bearing:

1. Re-read `AppHost.cs` — it is the single source of truth for declared resources
   (currently `server-windows`, `client-windows`, `server-linux`, `client-linux`).
2. Confirm render mode in `RuntimeMetricsLab.Web/Program.cs`.
3. Confirm actual emitted meters/instruments in the running Aspire Dashboard's
   **Metrics** tab, not from this document (see
   [`docs/metric-catalog.md`](metric-catalog.md)).
4. Confirm both Dockerfiles' `COPY` lists still match the AppHost's project
   references before assuming a container build will succeed.
