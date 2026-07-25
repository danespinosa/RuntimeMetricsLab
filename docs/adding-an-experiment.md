# Adding an experiment

An "experiment" in this repo is a small, focused piece of code that exercises a
specific .NET runtime behavior and exposes it through custom or built-in
OpenTelemetry metrics, viewable live in the Aspire Dashboard. This guide describes the
convention for `RuntimeMetricsLab.Experiments` — the project now contains real
experiment code (`Abstractions`, `Catalog`, `Coordination`, `Metrics`, `Workloads`);
follow the patterns already established there rather than starting from scratch.
Every experiment can be exercised on any of the four app resources
(`server-windows`, `client-windows`, `server-linux`, `client-linux` — see
[`docs/architecture.md`](architecture.md)), so design new experiments to be
platform-agnostic and compare their results across the Windows-process and
Linux-container pairs where relevant.

## 1. Decide the scope

Pick one narrow, demonstrable runtime behavior (see
[`docs/metric-catalog.md`](metric-catalog.md) for candidate topics: GC pressure,
thread-pool starvation, JIT/startup cost, resilience/retries, etc.). Avoid experiments
that mix multiple unrelated behaviors — the goal is a clean before/after signal on the
Dashboard, not a stress test.

## 2. Add the experiment code to `RuntimeMetricsLab.Experiments`

- Create one class per experiment (e.g. `AllocationPressureExperiment.cs`), not one
  giant static class. Replace/remove `Class1.cs` once real experiments exist.
- Define your own `System.Diagnostics.Metrics.Meter`, scoped to the experiment, e.g.:

  ```csharp
  namespace RuntimeMetricsLab.Experiments;

  public sealed class AllocationPressureExperiment : IDisposable
  {
      public const string MeterName = "RuntimeMetricsLab.Experiments.AllocationPressure";

      private readonly Meter _meter;
      private readonly Counter<long> _bytesAllocated;

      public AllocationPressureExperiment()
      {
          _meter = new Meter(MeterName, version: "1.0.0");
          _bytesAllocated = _meter.CreateCounter<long>(
              "experiment.allocation_pressure.bytes_allocated",
              unit: "By",
              description: "Bytes intentionally allocated by this experiment.");
      }

      public void Run(int arraySize)
      {
          var buffer = new byte[arraySize];
          _bytesAllocated.Add(arraySize); // no unbounded tags — see cardinality guidance
      }

      public void Dispose() => _meter.Dispose();
  }
  ```

- **Name instruments predictably**: `experiment.<topic>.<measurement>`
  (e.g. `experiment.thread_pool_starvation.queue_length`). This keeps custom
  experiment metrics visually distinct from built-in ASP.NET Core/runtime meters in
  the Dashboard's meter dropdown.
- **Apply the low-cardinality guidance from
  [`docs/metric-catalog.md`](metric-catalog.md)** to every tag you add — if you're not
  sure a tag is bounded, leave it off.

## 3. Register the meter with OpenTelemetry

Custom meters aren't emitted anywhere until OpenTelemetry is told to listen. Add an
`AddMeter("RuntimeMetricsLab.Experiments.<Name>")` call to the metrics pipeline —
either by extending `ConfigureOpenTelemetry` in
`RuntimeMetricsLab.ServiceDefaults/Extensions.cs` (if the meter should be visible from
every service) or, more likely, directly in the consuming project's own OTel setup if
it's specific to `ApiService` or `Web`:

```csharp
.WithMetrics(metrics =>
{
    metrics.AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(AllocationPressureExperiment.MeterName); // add this line
})
```

Register the experiment class itself with DI (`builder.Services.AddSingleton<...>()`)
in the project that will trigger it.

## 4. Expose a way to trigger the experiment

Add a minimal API endpoint in `RuntimeMetricsLab.ApiService` (or a page/button in
`RuntimeMetricsLab.Web` if it's more naturally UI-driven) that invokes the experiment
on demand — e.g. `POST /experiments/allocation-pressure?bytes=1000000`. Don't run
experiments automatically at startup; they should be opt-in so they don't pollute
baseline metrics for anyone just running the app.

## 5. Verify in the Aspire Dashboard, not just in code

1. `aspire run`, open the Dashboard.
2. Trigger the experiment via its endpoint.
3. Confirm the new meter appears in the **Metrics** tab's meter dropdown for the
   correct resource, and that instrument values change as expected.
4. Check **Structured logs**/**Traces** too if the experiment logs or creates spans —
   correlating a metric spike with a trace/log entry is often the actual point of the
   experiment.

## 6. Add a test

Add a test to `RuntimeMetricsLab.Experiments.Tests` (replace/extend `UnitTest1.cs`) that
exercises the experiment's *logic* deterministically (e.g. that `Run` allocates
roughly the requested size) — don't try to assert on OTel export behavior in a unit
test; that's what step 5's manual Dashboard check is for.

## 7. Update the docs

- Add or update the corresponding row in [`docs/metric-catalog.md`](metric-catalog.md),
  moving it from "hypothesized" to "currently emitted" once verified, and correct the
  meter/instrument names to what you actually observed (not what you assumed while
  writing the code).
- If the experiment changes the topology (new resource, new container), update
  [`README.md`](../README.md)'s topology section and
  [`docs/architecture.md`](architecture.md).
- If it opens new scope, note it in [`docs/roadmap.md`](roadmap.md).

## Checklist before opening a PR

- [ ] Experiment is a single, focused class in `RuntimeMetricsLab.Experiments`.
- [ ] Meter/instrument names follow `experiment.<topic>.<measurement>`.
- [ ] All tags reviewed against the low-cardinality guidance — no IDs, timestamps, or
      free text.
- [ ] Meter registered via `AddMeter(...)` in the relevant OTel pipeline.
- [ ] Triggered on demand via an endpoint/UI action, not automatically at startup.
- [ ] Verified live in the Aspire Dashboard (screenshot or description in the PR is
      helpful for reviewers who can't run it themselves).
- [ ] Unit test added for the experiment's logic.
- [ ] `docs/metric-catalog.md` updated to reflect the verified (not assumed) meter
      names/units.
