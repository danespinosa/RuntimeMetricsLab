namespace RuntimeMetricsLab.Analysis;

public static class AttributionMath
{
    public static double? AverageCores(double? cpuSeconds, double seconds) =>
        seconds > 0 && double.IsFinite(seconds) && cpuSeconds is >= 0 && double.IsFinite(cpuSeconds.Value)
            ? cpuSeconds / seconds : null;

    public static double? CpuMilliseconds(double? coveredCpuSeconds, double weight, double totalWeight) =>
        coveredCpuSeconds is >= 0 && double.IsFinite(coveredCpuSeconds.Value) && totalWeight > 0 &&
        double.IsFinite(totalWeight) && weight >= 0 && double.IsFinite(weight) && weight <= totalWeight
            ? 1000 * coveredCpuSeconds * weight / totalWeight : null;

    public static double? PerCall(double? cost, long? entered, long? completed, bool windowAligned) =>
        windowAligned && entered == completed && completed is > 0 && cost is >= 0 && double.IsFinite(cost.Value)
            ? cost / completed : null;
}
