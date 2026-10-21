# Understanding method CPU and allocation costs

This walkthrough describes real profiled reports. The native Windows
`attribution-windows` option uses stock Microsoft PerfView with explicit
scope consent/elevation. Its raw kernel trace is system-wide; the offline
method report selects the lab target. A successful live PerfView capture and
calibration are required before claiming support. Counter-only diagnostics and
previous failed ETLs are not successful profiled reports.
See [Windows status and authorization](windows-attribution.md).

The opt-in Linux attribution lab answers two related questions:

- **CPU:** The process consumed X cores on average. How much of that consumption can be attributed to a particular method?
- **Allocation:** How many managed bytes did a method allocate, how quickly, and at what average cost per completed call?

Counters establish process totals. Profiling identifies the stacks doing the work.
Explicit instrumentation in the teaching methods provides invocation counts. None
of these sources replaces the other two.

Use the [README startup instructions](../README.md#linux-attribution-mode) and the
prerequisite panel. On `/attribution`, choose a lesson and press **Run and analyze**.
The app collects a real OneCollect-backed `dotnet-trace collect-linux` profile and
automatically saves and analyzes it. Ordinary `dotnet-trace collect` thread sampling
is not substituted: sleeping threads can appear in that different kind of profile.

## Start with the measurement window

A capture includes attachment, idle baseline, measured work, recovery, and profiler
shutdown. Only the explicitly marked measured window belongs in the operation's
cost. CPU deltas, allocation events, elapsed time, and completed calls must describe
that same interval.

Read a result in this order:

1. Look at measured process CPU and approximate process allocation totals.
2. Select a method in the call tree, flame graph, or searchable table.
3. Compare its self and inclusive costs.
4. Read raw sample weights, allocation-event counts, invocation counts, and quality notices.
5. Follow the deterministic explanation back to the counter and profile inputs.

Closing a browser circuit does not cancel a job. Saved history and reports survive
application restarts. **Reanalyze saved bundle locally** creates a new report
revision without recollecting or changing the raw capture.

## CPU time, cores, and utilization

CPU time accumulates execution across the process's threads; it is not elapsed time.

```math
\mathrm{average\ process\ cores} =
\frac{\Delta\mathrm{user\ CPU\ seconds}+\Delta\mathrm{system\ CPU\ seconds}}
{\mathrm{monotonic\ elapsed\ seconds}}
```

A process consuming 40 CPU seconds in 10 elapsed seconds averages **4 cores**.
That is **400% of one core**, or **25% of 16 runtime-visible CPUs**. A single busy
worker can therefore consume about one full core while showing roughly 6.25% of
a 16-CPU process's capacity. These are logical CPU core-equivalents, not ownership
of particular physical cores.

The denominator is explicitly recorded `Environment.ProcessorCount`, not an assumed
`dotnet.process.cpu.count` built-in instrument or the Windows host's CPU count.
`ProcessorCount` is fixed at runtime startup and may round a fractional quota up.
Visible cgroup limits, affinity/cpuset, and runtime metadata are recorded separately.
The lab imposes no container CPU or memory quotas; inherited restrictions still matter.

## From process cores to method cores

For a compatible profile covering the process CPU pool:

```math
\mathrm{estimated\ method\ self\ cores} =
\mathrm{average\ process\ cores}\times
\mathrm{method\ exclusive\ CPU\ sample\ share}
```

If a process averages 4 cores and a method accounts for 25% of its exclusive
on-CPU weight, the method averages an estimated **1 core** over that interval.
Fractional and multicore method values are valid.

**Self** excludes descendants. **Inclusive** includes descendants and overlaps
other rows. If `HandleBatch` calls `TransformItem`, adding their inclusive CPU
would double-count the descendant's work. Only exclusive costs partition the
covered pool. Recursion is counted once per method in the flat inclusive summary.

Runtime, JIT, GC where identifiable, native/kernel, unresolved, and uncovered
costs are not redistributed to named lab methods. If absolute CPU attribution
is withheld, graphs show explicitly labeled raw sample weights instead.

### CPU per call

A method has no fixed price in "cores per call." Use estimated CPU milliseconds
per explicitly counted completed invocation:

```math
\mathrm{self\ CPU\ ms/call} =
\frac{\mathrm{estimated\ self\ CPU\ milliseconds}}{\mathrm{completed\ calls}},
\qquad
\mathrm{method\ cores} =
\frac{\mathrm{CPU\ ms/call}}{1000}\times\mathrm{calls/second}
```

This describes the observed workload, not a promise of linear scaling. Inputs,
concurrency, GC, instrumentation, and JIT state affect the result. Samples are not
invocations; the old spin iteration counter is not used. Framework/native methods
have no invented invocation denominator.

Known methods with no observed CPU samples, including waiting methods, remain in
the table. An unavailable cost is not "free." The details explicitly distinguish
self per-call cost from inclusive operation cost.

Tiered JIT can leave a resolved signature without a module on some frames. The
adapter reconciles it only when that exact full signature resolves to exactly one
module elsewhere in the same target capture, before constructing both flat and
call-path identities. Ambiguity remains visible and affected per-call costs are
withheld.

## Allocation is independent of CPU and retention

The allocation view estimates managed GC-heap bytes using allocation events,
never CPU percentages. It does not establish native allocation or RSS ownership.

```math
\mathrm{bytes/second} = \frac{\widehat{\mathrm{allocated\ bytes}}}{T},
\qquad
\mathrm{bytes/call} =
\frac{\widehat{\mathrm{allocated\ bytes}}}{\mathrm{completed\ calls}}
```

For illustration, **512 MiB in 10 seconds across 4,096 calls** means approximately
**51.2 MiB/second** and **128 KiB/call**. These are illustrative arithmetic values,
not a fabricated capture.

.NET 10 randomized `AllocationSampled` events are explicitly enabled; `gc-verbose`
alone does not enable them. Larger objects are more likely to be sampled. For the
verified geometric sampling model, each sampled object's byte weight is:

```math
p=\frac{1}{102400},
\qquad
\widehat{B}_{\mathrm{sample}} =
\frac{\mathrm{ObjectSize}}{1-(1-p)^{\mathrm{ObjectSize}}}
```

Object size includes headers and padding. Summing only sampled object sizes, or
counting samples as objects, would be incorrect. The analyzer strips the kernel/
CLR event-emission prefix to locate the allocation site; unresolved sites stay
visible rather than being charged to a convenient caller.

For selected synchronous lab helpers, same-thread
`GC.GetAllocatedBytesForCurrentThread` deltas independently calibrate managed
allocation, including descendants. These measurements never span `await` or
thread hops. Requested payload, actual managed allocation, and retained payload
are separate quantities.

| Memory figure | Meaning | Does not establish |
|---|---|---|
| Managed allocated bytes | Cumulative managed allocation activity | How much remains alive |
| Controlled retained payload | Explicitly held buffers and references | Generic heap ownership |
| Last-GC heap size/fragmentation | State observed at the latest collection | Instantaneous per-method memory |
| GC committed memory | Memory committed for GC use | All process physical memory |
| Working set | Physical memory mapped into the process | A method's allocation or retained bytes |

Churn can allocate heavily while retaining little. Release removes controlled
references, but GC and the OS can retain pages for reuse. RSS need not immediately
fall. The retention lesson's explicit GC is labeled **outside** measured work.
GC pause duration is not GC CPU time, and GC CPU is not automatically charged back
to allocation sites.

## Linked graphs and process charts

Tree and flame graph use the same saved model. Selecting a graph node preserves
its exact call path; selecting a method in the flat table locates a corresponding
path. **Zoom selected call path** and **Reset flame zoom** affect the viewport,
not the saved totals.

Flame width represents aggregate cost, not chronological time. CPU units can
switch between milliseconds and average cores; allocation units between bytes
and bytes/second. Per-call averages are not additive flame widths.

The viewport limits rendering to 2,000 rectangles; zoom and the expandable tree
expose additional paths without truncating the saved analysis. Buttons and the
searchable table provide a keyboard-accessible alternative.

Charts include raw cumulative counters and derived interval CPU cores, CPU
capacity percentage, and allocation rate. They use the saved target frequency and
CPU denominator, with elapsed-time axes and baseline/work/recovery guides. Missing
or reset observations are gaps. Very short intervals can amplify timing and
quantization noise; do not treat transient spikes as steady-state method costs or
hide them by clamping.

## Quality and calibration

Method costs are estimates from a profiled workload. Agreement achieved solely by
normalizing CPU shares to the counter is not an independent accuracy check.
The verified backend emits CPU count weights; nominal sampled CPU time uses the
recorded `ExpectedCPUSamplingRate` header (nanoseconds), not TraceLog's default
interval. The report compares this pre-scaling time with runtime CPU counters.
Allocation weights are independently compared with approximate cumulative
allocation; residuals are not forced to zero.

Normal per-call fields require a complete aligned window, positive matching
entered/completed counts, at least 20 applicable self samples/events, and the
independent population ratio within the documented 25% quality guard. This is
**not a statistical confidence interval**. Sparse allocation/retention CPU
profiles can legitimately fail the CPU guard while retaining useful allocation
estimates. Repeat captures and inspect method sample counts.

Zero parser-reported loss is **not proof of zero kernel collection loss**:
the pinned backend does not encode reliable kernel perf-loss counts in this
nettrace. That unknown is explicit. Missing markers/identity, reported loss,
unsupported formats, parser bounds, and interrupted captures fail visibly or
withhold costs. The syscall-traced compatibility probe is not final performance
calibration. An unprofiled baseline is needed before making an optimization
overhead claim; the report does not claim unprofiled production performance.

## Isolation, prerequisites, and troubleshooting

The workload, UI, and analysis worker run as UID 1654 without capabilities.
The collector shares only the target PID namespace and a lab-scoped `/tmp`.
There is no host PID namespace, Docker socket in the application, machine-wide
collection, arbitrary command/PID/provider API, unrestricted `--privileged`,
automatic kernel update, or host security change.

Linux 6.4+, `CONFIG_USER_EVENTS`, mounted tracefs, supported glibc/architecture,
and .NET 10+ are required. The prerequisite panel is not a substitute for a real
capture: ordinary `--probe` alone only validates runtime/IPC support.

The validated Docker 29/Aspire combination needs a fixed bootstrap because
Docker's pre-start archive setup cannot prepare a writable nested file bind under
an already read-only tracefs parent. Before any service listens, the bootstrap
checks private propagation, bind-remounts **only its private parent mount**
read-only, retains the writable `user_events_data` child, and irreversibly drops
temporary `SYS_ADMIN`/`SETPCAP`. The service verifies effective, permitted and
bounding sets contain only `PERFMON`, `SYS_PTRACE`, `DAC_OVERRIDE`, with empty
inheritable/ambient sets and no-new-privileges.

The host mount remains unchanged. A Docker administrator's `docker exec` helper
can inherit the initial bootstrap configuration; inspect the actual managed
collector and spawned tracer, not the helper's own capabilities.

| Failure | Action |
|---|---|
| Docker unavailable | Diagnose the existing WSL2 engine; do not install Docker Desktop |
| Runtime probe passes, native attach fails | Check the shared lab `/tmp`; pinned native lookup uses `/proc/<pid>/root/tmp` |
| `EROFS` on `user_events_data` | Check the narrow writable file bind and fail-closed bootstrap, not host permissions |
| Missing symbols/markers or partial trace | Keep raw artifacts and inspect local diagnostics; never substitute off-CPU sampling |
| Storage full | Export/manage lab storage explicitly; old captures are never silently deleted |
| Interrupted process | Recovered jobs remain failed/partial, never silently rerun; finalized bundles can be reanalyzed |

See [architecture](architecture.md), [metric catalog](metric-catalog.md), and the
validation commands in the README.

## References

- [.NET runtime metrics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime)
- [Environment.ProcessorCount](https://learn.microsoft.com/en-us/dotnet/api/system.environment.processorcount?view=net-10.0)
- [dotnet-trace collection modes](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace)
- [.NET 10 randomized allocation sampling](https://github.com/dotnet/runtime/blob/v10.0.8/docs/design/features/RandomizedAllocationSampling.md)
- [TraceEvent 3.2.8](https://github.com/microsoft/perfview/releases/tag/v3.2.8)
