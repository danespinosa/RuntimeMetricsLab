using System.Diagnostics.Tracing;

namespace RuntimeMetricsLab.Experiments.Attribution;

[EventSource(Name = "RuntimeMetricsLab-Attribution")]
public sealed class AttributionMarkers : EventSource
{
    public static readonly AttributionMarkers Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Boundary(string runId, string phase, long monotonicTicks) => WriteEvent(1, runId, phase, monotonicTicks);

    [Event(2, Level = EventLevel.Informational)]
    public void Identity(string runId, string instanceId, int processId, string processStartUtc, string moduleMvid) =>
        WriteEvent(2, runId, instanceId, processId, processStartUtc, moduleMvid);
}
