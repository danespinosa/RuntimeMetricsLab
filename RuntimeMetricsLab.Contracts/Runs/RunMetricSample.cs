namespace RuntimeMetricsLab.Contracts.Runs;

/// <summary>A bounded per-run measurement summary returned by the experiment API.</summary>
public sealed record RunMetricSample(string Name, double Value, string Unit);
