# Roadmap

This roadmap tracks how RuntimeMetricsLab grows into a
comprehensive .NET 10 runtime-metrics catalog. Phase numbers are directional, not
committed dates — reorder/merge as real experiments land.

## Linux method attribution

The opt-in attribution implementation now provides controlled CPU, allocation,
retention/release and mixed-waiting lessons with real OneCollect collection,
automatic isolated TraceEvent analysis, durable history and local downloads.
Reports connect measured process cores to estimated method cores and independently
weighted allocation bytes/rates/per-call costs. Linked tree/flame views and raw/
interval charts share the recorded measurement window and preserve uncertainty.

The four-resource standard topology remains the default. Attribution does not
impose CPU/memory quotas or implement cache/bandwidth attribution, arbitrary
production-process profiling, generic retained/native/RSS ownership, or exact
unobserved per-invocation costs. Kernel perf-loss counts remain explicitly
unavailable in the pinned backend. See [the walkthrough](attribution-lab.md) and
the real-capture validation script in the README.

## Native Windows continuation

Implemented opt-in native Windows topology, platform/storage/provenance
boundaries, non-elevated counter diagnostics, target-only allocation probe,
consent-gated stock Microsoft PerfView launcher, offline analysis adapter, and boundary/lifecycle tests.
Native workload/counter/allocation evidence is verified. **Full Windows on-CPU
collection and its elevated end-to-end workflow are not yet live-validated**:
they require explicit approval of local system-wide raw kernel traces and
launcher/PerfView-only elevation. The superseded custom ETW engine is removed.
This permission gate is not hidden by EventPipe
thread-time samples or counter-only success. See [Windows status](windows-attribution.md).

The Windows primary UX now waits for a deliberate in-app scope decision and
Windows UAC approval, then authenticates the real profiler before starting work.
The waiting/denial/cancel/timeout/restart/late-completion paths are covered by
safe injected fixtures; native UI validation deliberately does not accept scope
or UAC on the user's behalf. Elevated CPU compatibility remains the external gate.

## Phase 0 — Scaffold (superseded)

- Aspire Starter App topology: `AppHost`, `ServiceDefaults`, `ApiService`, `Web`
  (Blazor Interactive Server), plus placeholder `Contracts` and `Experiments`
  projects and their test project.
- Only built-in instrumentation was active: ASP.NET Core, `HttpClient`, and .NET
  runtime meters via `OpenTelemetry.Instrumentation.*` 1.15.x, wired centrally in
  `ServiceDefaults/Extensions.cs`.
- Documentation described the topology as two declared resources (`apiservice`,
  `webfrontend`) plus the AppHost/Dashboard processes.

## Phase 0.5 — Platform orchestration (current state)

- **The AppHost now declares four real application resources** instead of two:
  `server-windows`/`client-windows` (native Windows processes, via
  `builder.AddProject<...>`) and `server-linux`/`client-linux` (Linux containers, via
  `builder.AddDockerfile(...)` against the multi-stage `Dockerfile`s in
  `RuntimeMetricsLab.ApiService/` and `RuntimeMetricsLab.Web/`). Same `ApiService`/`Web`
  project code runs on both platforms per pair — see
  [`docs/architecture.md`](architecture.md) and the README's topology section.
- **Containers are now required to run this app at all** — `server-linux`/
  `client-linux` need the Docker CLI backed by the WSL2 Docker Engine
  (`DOCKER_HOST=tcp://127.0.0.1:2375`, never Docker Desktop). This was originally
  slated for Phase 3 below; it landed earlier than planned, so the Phase 3 container
  milestone is already satisfied at the infrastructure level (see the updated bullet
  in Phase 3).
- `ServiceDefaults` now tags every resource with `runtime.metrics.lab.role` /
  `runtime.metrics.lab.platform` OTel resource attributes and registers a broader set
  of built-in meters (`Microsoft.AspNetCore.Components*`, `Grpc.AspNetCore.Server`,
  etc.) plus the `RuntimeMetricsLab.Experiments` meter/trace source — `Experiments`
  now contains real code (`Abstractions`, `Catalog`, `Coordination`, `Metrics`,
  `Workloads`), no longer an empty placeholder.
- Documentation (this doc set) now describes the real four-resource, two-platform
  topology and flags metric names as hypothesis-pending-verification where not yet
  confirmed live.

## Phase 1 — Core experiment scope

Goal: prove out the experiment pattern end-to-end with a small number of
representative experiments, establishing conventions before scaling out.

- Implement 2–3 experiments covering distinct built-in meter families already
  wired up (no new packages needed): allocation/GC pressure and thread-pool
  starvation are good first candidates since `AddRuntimeInstrumentation` already
  surfaces `System.Runtime`.
- Run each experiment on all four resources (or at minimum one server pair and one
  client pair) so Windows-process vs. Linux-container comparison is exercised from
  the start, not bolted on later.
- Convert every "likely" row these experiments touch in
  [`docs/metric-catalog.md`](metric-catalog.md) from hypothesized to verified,
  with names/units confirmed against the live Dashboard, per resource.
- Add experiment-triggering endpoints to `ApiService`, and, where the workload is
  naturally interactive, a small UI in `Web`.

## Phase 2 — Cross-cutting correlation

Goal: move beyond isolated metrics to demonstrate metrics-traces-logs correlation,
which is most of the practical value of OpenTelemetry over raw counters.

- Add tracing spans around experiment execution so a metric spike can be traced
  back to a specific request/operation in the Dashboard, on any of the four resources.
- Add an HTTP-resilience experiment exercising
  `Microsoft.Extensions.Http.Resilience` (retries/circuit breakers) between a client
  and its paired server, correlating retry metrics with trace spans showing the
  retried calls; compare behavior across the Windows and Linux pairs.
- Verify the `Microsoft.AspNetCore.Components.Server.Circuits` meter now listed in
  `ServiceDefaults`' `BuiltInMeterNames` actually emits on `client-windows` and
  `client-linux`, and document its real instrument names in
  [`docs/metric-catalog.md`](metric-catalog.md) (currently unconfirmed).
- Re-verify all recorded tool/package versions in the README against what's
  actually installed; note any drift found.

## Phase 3 — Comprehensive catalog

Goal: expand from the narrow core set of experiments into a broad, browsable
reference covering the practical range of .NET 10 runtime-observable behavior —
this is the scope-expansion milestone referenced as **#3** for this project.

- Cover the remaining candidate topics from
  [`docs/metric-catalog.md`](metric-catalog.md) not yet implemented: JIT/startup
  cost, Kestrel connection metrics, DNS/name-resolution latency, and any others
  identified in Phases 1–2.
- ~~Add a Linux container variant of at least one GC/allocation experiment~~ —
  **done ahead of schedule**: `server-linux`/`client-linux` now run as Linux
  containers on every `aspire run` (see Phase 0.5). The remaining Phase 3 work here
  is the *experiment content*, not the infrastructure: a separately approved,
  future quota-changing experiment could demonstrate cgroup-aware GC
  behavior against the unconstrained `-windows` pair. Attribution mode deliberately
  imposes no container quotas. See
  [`docs/architecture.md`](architecture.md) and the container-limits row in
  [`docs/metric-catalog.md`](metric-catalog.md).
- Consider exemplars (metric-to-trace links) once the OTel/.NET tooling around
  them is verified stable for the versions in use.
- Turn `docs/metric-catalog.md` into a fully-verified reference table (no
  remaining "hypothesized/unconfirmed" rows) covering every experiment shipped by
  this point, organized by runtime subsystem (GC, thread pool, networking,
  hosting, components) rather than by experiment, with results broken out by all
  four resources.
- Revisit whether `RuntimeMetricsLab.Contracts` needs to grow further or whether it
  should be folded into `Experiments` — decide based on actual duplication observed,
  not speculatively.

## Ongoing, every phase

- Keep the version table in [`README.md`](../README.md) current; note drift
  explicitly rather than silently updating numbers without comment, since drift
  itself (what changed, and whether metric names moved) is useful lab data.
- Every new or changed instrument gets the low-cardinality review described in
  [`docs/metric-catalog.md`](metric-catalog.md) before merging.
- Every architecture change (new resource, new render mode, new container) gets
  reflected in [`docs/architecture.md`](architecture.md) and the README's
  topology section in the same change, not as a follow-up.
