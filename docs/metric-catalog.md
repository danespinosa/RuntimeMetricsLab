# Metric catalog

The runtime table below is verified against the pinned Linux attribution stack
(.NET 10.0.10, OpenTelemetry runtime instrumentation 1.15.1). Other candidate tables
remain hypotheses until separately verified. Runtime instrument availability is
recorded explicitly; missing observations are not synthesized.

## Verified attribution observations

Native Windows .NET 10.0.12 exposes the same primary cumulative CPU/allocation
and working-set observations used by the shared sampler. Windows normalized
capacity still uses recorded `Environment.ProcessorCount`; processor groups,
default CPU sets and available job restrictions are separately labeled metadata.
No cgroup path or single 64-bit affinity mask is substituted for full Windows
capacity. Counter-only native diagnostics do not provide method CPU attribution.
The separately verified target-only EventPipe allocation probe is explicitly
allocation-only. [Windows profiling status](windows-attribution.md).

`RuntimeSampler` reads actual `System.Runtime` instruments, retaining units, tags,
cumulative values, monotonic ticks and display UTC. The compatibility CPU endpoint
uses this shared sampler, exposes availability/staleness flags, and no longer uses
wall-clock intervals, clamping, or working-set fallbacks for its calculations.

| Instrument | Observed unit | Interpretation |
|---|---|---|
| `dotnet.process.cpu.time` | `s` | Cumulative CPU, separately tagged `cpu.mode=user` and `system` |
| `dotnet.process.memory.working_set` | `By` | Process physical mappings, not per-method ownership |
| `dotnet.gc.heap.total_allocated` | `By` | Approximate cumulative managed allocation |
| `dotnet.gc.collections` | `{collection}` | Cumulative counts by `gc.heap.generation=gen0/gen1/gen2` |
| `dotnet.gc.last_collection.heap.size` | `By` | Last-GC size by gen0/gen1/gen2/loh/poh, including fragmentation |
| `dotnet.gc.last_collection.heap.fragmentation.size` | `By` | Last-GC fragmentation by generation |
| `dotnet.gc.last_collection.memory.committed_size` | `By` | Last-GC committed memory |
| `dotnet.gc.pause.time` | `s` | Accumulated suspension duration, not GC CPU |
| `dotnet.thread_pool.thread.count` | `{thread}` | Thread-pool thread count |
| `dotnet.thread_pool.queue.length` | `{work_item}` | Queued work |
| `dotnet.jit.compilation.time` | `s` | Cumulative JIT compilation time |
| `dotnet.jit.compiled_methods` | `{method}` | Compiled method count |
| `dotnet.jit.compiled_il.size` | `By` | Compiled IL bytes |

CPU normalization uses **`Environment.ProcessorCount` metadata**, not an invented
or required `dotnet.process.cpu.count` built-in. Additional instruments may appear
in the pinned instrumentation stack; the analyzer does not depend on that name.
Cgroup `cpu.max`, effective cpuset, memory limit/current usage, CPU throttling data,
runtime/build metadata and affinity are supplemental environment observations,
not built-in metric substitutes.

The attribution page preserves last-GC freshness caveats, raw samples, and derived
interval cores/capacity percentage/allocation rate using the target's saved
frequency. Its method allocation estimator uses randomized `AllocationSampled`
events, not payload counters or CPU shares. See [the walkthrough](attribution-lab.md).

## Remaining catalog verification

> .NET
> runtime meter/instrument names, units, and even meter *existence* have changed
> across .NET releases (several were renamed/added between .NET 8 and .NET 9/10) and
> the `OpenTelemetry.Instrumentation.*` packages evolve independently of the runtime.
> **Every row below must be confirmed against the live Aspire Dashboard (Metrics tab)
> or `dotnet-counters monitor` against a running resource — `server-windows`,
> `client-windows`, `server-linux`, or `client-linux` — before you depend on it in an
> experiment, alert, or blog post.** Treat names/units as "likely, pending
> verification," not fact. With four resources now running the same code across two
> platforms, also verify whether a given instrument's *values* (not just its name)
> differ between the `-windows` and `-linux` variant under the same workload — that
> comparison is the point of this lab.

## How to verify a row yourself

1. Run `aspire run`, open the Dashboard, pick a resource, open **Metrics**.
2. Pick a meter from the dropdown — if it's not listed, it isn't currently emitting
   (either not instrumented, or not registered in `ServiceDefaults`).
3. Alternatively, from a terminal: `dotnet-counters monitor -n <process-name>` (install
   via `dotnet tool install -g dotnet-counters` if missing) lists live EventCounters,
   which underlie several `System.Runtime` meter instruments.
4. Cross-check the instrument's unit and description shown in the Dashboard tooltip
   against what's written here — update this file if they differ.

## Currently emitted meters (wired in `ServiceDefaults/Extensions.cs`)

These are active today, on **all four resources** (`server-windows`, `client-windows`,
`server-linux`, `client-linux`), via `AddAspNetCoreInstrumentation()`,
`AddHttpClientInstrumentation()`, `AddRuntimeInstrumentation()`, and an explicit
`AddMeter(...)` list covering `Microsoft.AspNetCore.Components*`, `Grpc.AspNetCore.Server`,
and `RuntimeMetricsLab.Experiments` — no per-resource wiring is required to see these in
the Dashboard, and every emitted metric carries `runtime.metrics.lab.role` /
`runtime.metrics.lab.platform` resource attributes you can use to filter by
server/client and Windows/Linux.

| Meter (likely name) | Example instruments (likely) | Source |
|---|---|---|
| `System.Runtime` | `dotnet.gc.heap.total_allocated`, `dotnet.gc.collections`, `dotnet.process.cpu.time`, `dotnet.thread_pool.thread.count`, `dotnet.thread_pool.queue.length`, `dotnet.jit.compiled_methods` | .NET runtime `System.Diagnostics.Metrics` observations with `OpenTelemetry.Instrumentation.Runtime` 1.15.1 |
| `Microsoft.AspNetCore.Hosting` | `http.server.request.duration`, `http.server.active_requests` | `OpenTelemetry.Instrumentation.AspNetCore` 1.15.2, built on ASP.NET Core's own built-in `Microsoft.AspNetCore.Hosting`/`Microsoft.AspNetCore.Server.Kestrel` meters (native since .NET 8) |
| `System.Net.Http` | `http.client.request.duration`, `http.client.active_requests` | `OpenTelemetry.Instrumentation.Http` 1.15.1, wrapping `System.Net.Http`'s built-in `HttpClient` meter |
| `Microsoft.AspNetCore.Components.Server.Circuits` | circuit count/duration instruments (names unconfirmed) | Explicitly listed in `ServiceDefaults`' `BuiltInMeterNames`; only emits on `client-windows`/`client-linux` (Blazor Interactive Server) — verify names against the Dashboard before use |
| `RuntimeMetricsLab.Experiments` | Custom `experiment.*` instruments defined per experiment | Registered via `AddMeter("RuntimeMetricsLab.Experiments")` and `AddSource("RuntimeMetricsLab.Experiments")` in `ServiceDefaults`; see [`docs/adding-an-experiment.md`](adding-an-experiment.md) for the naming convention and current experiment inventory in the `RuntimeMetricsLab.Experiments` project |

## Hypothesized meters for "core experiment" topics (require runtime verification)

These map likely candidate .NET 10 built-in meters/instruments to the kinds of runtime
behaviors a lab experiment would want to demonstrate, evaluated **per platform pair**
(`server-windows` vs. `server-linux`, `client-windows` vs. `client-linux`). Treat every
row as a hypothesis to confirm against your own running resources and current
`RuntimeMetricsLab.Experiments` content, not as documentation of guaranteed behavior.

| Experiment topic | Likely meter | Likely instrument(s) | Verify via |
|---|---|---|---|
| GC pressure / allocation rate | `System.Runtime` | `dotnet.gc.heap.total_allocated` (counter, bytes), `dotnet.gc.collections` (counter, tagged by `gc.heap.generation`) | Runtime names verified above; platform comparison remains a separate experiment |
| GC pause time / server GC vs. workstation GC | `System.Runtime` | `dotnet.gc.pause.time` (if present) or EventCounter `time-in-gc` | `dotnet-counters monitor`; compare `<ServerGarbageCollection>` on/off in the target `.csproj`, and Windows vs. Linux (cgroup-aware) behavior |
| Thread pool starvation | `System.Runtime` | `dotnet.thread_pool.thread.count`, `dotnet.thread_pool.queue.length` | Drive load against an endpoint with blocking `Task.Result`/sync-over-async calls and watch queue length climb on both platform variants |
| JIT / startup cost | `System.Runtime` | `dotnet.jit.compiled_methods`, `dotnet.jit.compilation.time` | Compare cold start vs. warmed process; container cold start (`server-linux`/`client-linux`) is a natural point of comparison against native process startup |
| HTTP request latency under load | `Microsoft.AspNetCore.Hosting` | `http.server.request.duration` (histogram, tagged by route/status) | Load-test each server resource, watch histogram buckets, not just averages |
| Outbound call resilience (Polly via `Microsoft.Extensions.Http.Resilience`) | `System.Net.Http` + resilience-specific meter (name TBD — check `Microsoft.Extensions.Http.Resilience` 10.6.0 docs/source) | Retry/circuit-breaker counters, `http.client.request.duration` | Inject failures into a server resource and observe its paired client's `HttpClient` retries in traces + metrics together |
| Blazor Interactive Server circuits | `Microsoft.AspNetCore.Components.Server.Circuits` | circuit count/duration instruments, if present | Open/close browser tabs against `client-windows`/`client-linux`, watch Dashboard for the components-server meter; if names differ from expectation, correct this row |
| Kestrel connection metrics | `Microsoft.AspNetCore.Server.Kestrel` | connection count/duration instruments | Dashboard meter dropdown for any of the four resources; confirm whether `AddAspNetCoreInstrumentation` surfaces these or whether they need separate opt-in |
| DNS / name resolution latency | `System.Net.NameResolution` | resolution duration histogram | Only relevant once service discovery resolves real DNS names rather than local Aspire-injected endpoints; also present in `BuiltInMeterNames` today |
| Container resource limits vs. GC heap sizing | OS/cgroup metadata plus runtime observations | Visible inherited limits and effective cpuset | Attribution mode records inherited restrictions but imposes no CPU or memory quotas. Deliberate quota-changing experiments are separate, future opt-ins. |

## Low-cardinality tag guidance

Any instrument — built-in or custom — records **tags (attributes)** alongside each
measurement. High-cardinality tags are the single most common way to break a metrics
pipeline (Dashboard UI, OTLP collector memory, or a real backend's cost):

- **Never tag with unbounded values**: user IDs, request IDs, correlation/trace IDs,
  raw URLs/paths containing IDs, timestamps, or free-text. Those belong on **traces**
  or **logs**, not metric tags.
- **Prefer small, closed sets**: HTTP method, status code *class* (`2xx`/`4xx`/`5xx`
  or exact code if the set is bounded), route template (`/weatherforecast`, not the
  resolved URL), GC generation (`0`/`1`/`2`), success/failure boolean.
- **Route templates, not raw paths** — this is why `AddAspNetCoreInstrumentation`
  tags `http.route` with the route pattern rather than the resolved URL; follow the
  same rule for any custom endpoint tag.
- The `runtime.metrics.lab.role` (`server`/`client`) and `runtime.metrics.lab.platform`
  (`windows-process`/`linux-container`) resource attributes are a good example of
  correctly bounded tags — exactly 2×2 = 4 combinations, fixed at deployment time, safe
  to filter/group by in the Dashboard or any backend.
- **Budget your tag combinations.** A histogram with `route × status_class × method`
  is fine (dozens of combinations); one that adds `user_id` turns into millions of
  time series.
- **When in doubt, start with zero tags** and add them only when you have a concrete
  question that requires the breakdown — it's much safer to under-tag and add later
  than to over-tag and need a backend migration to fix cardinality blowup.

See [`docs/adding-an-experiment.md`](adding-an-experiment.md) for where this guidance
applies when writing new instruments.
