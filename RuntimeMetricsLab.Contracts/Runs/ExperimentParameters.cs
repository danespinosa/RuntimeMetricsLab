namespace RuntimeMetricsLab.Contracts.Runs;

/// <summary>
/// Flat, low-cardinality bag of numeric knobs shared across every experiment. Each experiment
/// only reads the subset of fields relevant to it; unused fields are simply ignored. Keeping a
/// single shared shape avoids polymorphic request bodies and keeps the wire contract simple.
/// </summary>
public sealed record ExperimentParameters
{
    public int? DurationSeconds { get; init; }
    public int? IntensityPercent { get; init; }
    public int? WorkerCount { get; init; }
    public long? AllocationSizeBytes { get; init; }
    public int? AllocationsPerSecond { get; init; }
    public int? BlockedThreadCount { get; init; }
    public int? PayloadSizeBytes { get; init; }
    public int? DelayMilliseconds { get; init; }
    public int? FailureRatePercent { get; init; }
    public int? FanOutCount { get; init; }
    public int? MessageCount { get; init; }
    public int? MessageSizeBytes { get; init; }
}
