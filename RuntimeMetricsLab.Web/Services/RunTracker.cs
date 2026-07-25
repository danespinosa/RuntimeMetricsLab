using RuntimeMetricsLab.Web.Api;

namespace RuntimeMetricsLab.Web.Services;

/// <summary>A run started from this Blazor circuit, kept client-side so the Compare page can offer a quick picker
/// without requiring a "list runs" endpoint (the described contract only supports get-by-id).</summary>
public sealed record TrackedRun(string RunId, string ExperimentId, MetricCategory Category, string Preset, string? Target, DateTimeOffset StartedAtUtc);

/// <summary>
/// Per-circuit (scoped) memory of runs started during the current session. This is intentionally
/// in-memory only — it resets if the circuit is lost — and exists purely to make the Compare page
/// convenient; it is never treated as a source of truth for run state (GetRunAsync always is).
/// </summary>
public sealed class RunTracker
{
    private readonly List<TrackedRun> _runs = new();

    public event Action? Changed;

    public IReadOnlyList<TrackedRun> Runs => _runs;

    public void Track(TrackedRun run)
    {
        _runs.RemoveAll(r => r.RunId == run.RunId);
        _runs.Insert(0, run);
        if (_runs.Count > 25)
        {
            _runs.RemoveRange(25, _runs.Count - 25);
        }

        Changed?.Invoke();
    }

    public void Remove(string runId)
    {
        if (_runs.RemoveAll(r => r.RunId == runId) > 0)
        {
            Changed?.Invoke();
        }
    }
}
