namespace RuntimeMetricsLab.Contracts.Diagnostics;

/// <summary>A point-in-time process sample collected from built-in System.Runtime metrics.</summary>
public sealed record RuntimeMetricSample(
    DateTimeOffset Timestamp,
    double CpuUtilizationPercent,
    double CpuTimeSeconds,
    long WorkingSetBytes,
    int ProcessorCount);
