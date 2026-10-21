# Native Windows attribution with stock PerfView

Windows collection invokes the **actual Microsoft-signed PerfView 3.2.8
executable**. PerfView owns its ETW sessions, sampled CPU stacks, buffering,
CLR rundown and merge. The former custom session/rundown/relogger engine has
been removed. TraceEvent 3.2.8 is used only for offline analysis.

The controlled native workload, web UI and analysis worker remain non-elevated.
Only the existing small authenticated launcher and its stock PerfView child run
elevated. Linux continues to use `dotnet-trace collect-linux`/OneCollect; the
four-resource Windows/Linux comparison remains the default.

## Verification status

The pinned executable's official release hash and Microsoft Authenticode
signature are verified. Its real command-line help accepts the fixed options
below. The replacement builds and is wired into the existing one-button
capture/analysis/history UI. **A successful real PerfView capture and independent
CPU/allocation calibration are required before claiming Windows end-to-end
attribution works.** Fixture tests and the retired engine's traces are not that
proof.

The previous failed Windows captures remain immutable and failed. They are still
downloadable and inspectable using their legacy format/provenance. They are never
relabeled as PerfView output or silently converted into successful reports.

## Setup and launch

Run from this worktree in a normal, non-administrator PowerShell:

```powershell
.\tools\Get-PerfView.ps1
$env:RuntimeMetricsLab__Mode = "attribution-windows"
aspire start --isolated --non-interactive
aspire wait client-windows --non-interactive
aspire describe client-windows --non-interactive
```

The setup script downloads only the official release, verifies its SHA-256 and
Microsoft signature, and stores it under ignored
`artifacts\perfview\3.2.8\PerfView.exe`. It does not modify an existing machine-wide
PerfView installation, start a profiler, request UAC or accept a license. The
launcher checks the pinned hash again and holds a read-only, non-write-sharing
handle while invoking the executable.

SHA-256:
`E6B89A6DA0FE7DA5F64302306153C6AAC7A4BE67C800426A91AE014093E4DF2D`

Open the displayed client URL at `/attribution`. Choose a finite lesson and
**Run and analyze**, review the scope, then explicitly accept and respond to
Windows UAC yourself. The dialog is for `RuntimeMetricsLab.Collector.exe`, the
fixed lifecycle/authentication launcher; it invokes stock `PerfView.exe`.
Do not elevate the AppHost, workload, UI or analysis.

## Important raw-trace scope

**Stock PerfView persists system-wide kernel CPU, process, thread and image
events in the private local ETL.** This may include other processes' names,
command lines, loaded-image paths and sampled stacks. `/FocusProcess:<PID>`
restricts **non-kernel** CLR/marker providers and rundown, not kernel CPU.
It does not make the raw trace target-only.

The app displays this scope before UAC. Old grants for the retired target-only
relogger are incompatible with registration schema 3 and cannot authorize
PerfView. A new registration requires fresh consent. Only the selected target's
PID, exact process start, build identity and run-window markers are accepted for
the method report. Other processes' methods are excluded from the report, but
the raw downloaded bundle still has the disclosed system-wide scope.

All traces, logs, symbol files, analysis indexes and reports stay local. Symbol
paths supplied to PerfView are local directories, not external symbol servers.
Native symbols may remain unresolved and are retained as unknown buckets. There
are no uploads, external analysis calls or production-overhead claims.

## Fixed stock command

The launcher uses only validated, internally generated values, equivalent to:

```text
PerfView.exe /LogFile:<private-capture>\perfview.log
  /DataFile:<private-capture>\trace.etl
  /SessionName:RuntimeMetricsLab.PerfView.<lab>.<capture-guid>
  /FocusProcess:<verified-existing-target-pid>
  /MaxCollectSec:<lesson-seconds-plus-eight>
  /BufferSizeMB:256 /CircularMB:500 /RundownTimeout:120 /CpuSampleMSec:1
  /KernelEvents:Process,Thread,ImageLoad,Profile
  /ClrEvents:Default,AllocationSampling
  /Providers:*RuntimeMetricsLab-Attribution /NoView /Zip:false collect
```

This attaches to the existing non-elevated target; it does not use `run` to
launch/elevate the workload. `Profile` is sampled on-CPU work, not ThreadTime or
EventPipe sampled-thread-time. CLR randomized allocation events use the .NET 10
`AllocationSampling` keyword independently of CPU.

PerfView's 256-MiB buffers, 500-MiB circular kernel file and 120-second rundown
bound are its stock defaults, not workload quotas. The user-mode circular file
can be larger. The lab checks private free space and bounds input/analysis
resources; an oversized or incomplete trace fails explicitly. No CPU or memory
quota is imposed on the workload.

Each capture gets its own session name; PerfView derives the kernel name from
it. Before collecting, the launcher uses stock `ListSessions` and refuses
conflicting PerfView/trigger sessions rather than stopping another profiler.
The lab never sends a machine-wide stop, adopts NT Kernel Logger, manually
enables providers, changes Windows profiling rights, or implements an ETW
watchdog.

## Approval, cancellation and completion

Page load, polling, reload and initial queueing do not elevate or collect.
Explicit server-circuit consent uses the existing single-use, job/generation-bound
challenge. The private control API and same-owner medium-integrity pipe keep
credentials off command lines and authenticate actual client/server identities.
Duplicate acceptance cannot launch twice; late UAC acceptance after cancellation,
expiry or restart cannot start a stale workload.

Scope consent is bounded to five minutes, UAC to two minutes, and authenticated
launcher readiness to 30 seconds. These waits do not consume the workload timer.
The approved launcher can be reused for at most one hour. A returned process
handle is not collection readiness: the target waits for its actual marker
provider before beginning the measured window.

The app never passes `/AcceptEULA`. If PerfView reports that its EULA has not been
accepted, the user must open the verified stock tool, review and accept it
themselves before another run.

**Cancellation stops the workload immediately.** The stock collector finishes
its finite `MaxCollectSec` interval, then performs its own rundown and merge.
The UI remains responsive and retains the cancelled trace as incomplete.
Finalization has a separate bound because rundown/merge occur *after* collection.
No new capture is admitted while the previous one is active. A tool timeout or
interrupted partial capture fails closed and requires inspection of its exact
saved log/session; the lab does not blindly kill PerfView and leave ETW running.

## Storage and analysis

| Location | Purpose |
|---|---|
| `%LOCALAPPDATA%\RuntimeMetricsLab\Attribution\<lab>\jobs` | Durable jobs, target counters, immutable bundle manifests and report revisions |
| `%LOCALAPPDATA%\RuntimeMetricsLab\Attribution\<lab>\collector-registration.json` | Private per-start launcher registration |
| `%LOCALAPPDATA%\RuntimeMetricsLab\Attribution\<lab>\profiler-approvals` | Existing bounded consent/UAC state |
| `%PROGRAMDATA%\RuntimeMetricsLab\Attribution\<lab>\captures` | Administrator-owned private PerfView ETLs/logs/configuration; requesting owner can read |

New bundles record the `PerfViewEtw` format and actual raw scope.
`perfview.json` records tool version/hash, exact arguments, target identity and
session name; its checksum is part of `ArtifactHashes`. `perfview.log`,
`session-list.log`, stdout/stderr, merged ETL and target counters accompany it.
Download and reanalysis use the same artifact list. Legacy Windows ETW and Linux
bundles keep their old required artifacts.

Offline TraceEvent processing reads loss metadata and selects target events
before constructing observations/method reports. Missing CPU/window/identity,
nonzero reported loss or incomplete collection cannot become a ready report.
The configured stock sampling interval is checked through independent
sample-weight-versus-process-CPU calibration, not by forcing the residual to
zero. CPU and allocation calibration remain independent.

Process and method core-equivalents share the same monotonic window. Per-call
values use measured completed lab calls, never sample counts. Heap, working set
and controlled retention remain process-level observations, not invented
per-method retained/native memory ownership. See the
[shared walkthrough](attribution-lab.md).

## References

- [Official PerfView 3.2.8 release](https://github.com/microsoft/perfview/releases/tag/v3.2.8)
- [Stock command-line arguments](https://github.com/microsoft/perfview/blob/v3.2.8/src/PerfView/CommandLineArgs.cs)
- [Stock collection/rundown/merge implementation](https://github.com/microsoft/perfview/blob/v3.2.8/src/PerfView/CommandProcessor.cs)
- [Stock EULA and batch-mode handling](https://github.com/microsoft/perfview/blob/v3.2.8/src/PerfView/App.cs)
- [ETW process-filter semantics](https://learn.microsoft.com/en-us/windows/win32/api/evntprov/ns-evntprov-event_filter_descriptor)
