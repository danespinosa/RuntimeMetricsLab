namespace RuntimeMetricsLab.Contracts.Catalog;

/// <summary>
/// Inclusive bounds and default value for a single numeric knob. Used both to describe the
/// catalog entry to clients and to validate incoming start requests server-side.
/// </summary>
public sealed record ParameterBound(int Min, int Max, int Default)
{
    public bool IsInRange(int value) => value >= Min && value <= Max;
}
