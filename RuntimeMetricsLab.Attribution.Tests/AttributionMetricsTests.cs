using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Attribution.Tests;

public sealed class AttributionMetricsTests
{
    [Fact]
    public void Value_SumsMatchingMetricAndFiltersCpuModeWithoutIncludingOtherMetrics()
    {
        var point = SyntheticCapture.CounterPoint(10000, "measure-start", 1.25, 0.75, 512);
        point = point with
        {
            Values =
            [
                .. point.Values,
                new("dotnet.process.cpu.time", 0.5, "s", new() { ["cpu.mode"] = "user" }),
                new("dotnet.process.memory.working_set", 999999, "By", new())
            ]
        };

        CostAssert.Equal(2.5, AttributionMetrics.Value(point, "dotnet.process.cpu.time"));
        CostAssert.Equal(1.75, AttributionMetrics.Value(point, "dotnet.process.cpu.time", "cpu.mode", "user"));
        CostAssert.Equal(0.75, AttributionMetrics.Value(point, "dotnet.process.cpu.time", "cpu.mode", "system"));
        CostAssert.Equal(512, AttributionMetrics.Value(point, "dotnet.gc.heap.total_allocated"));
        Assert.Null(AttributionMetrics.Value(point, "missing"));
        Assert.Null(AttributionMetrics.Value(point, "dotnet.process.cpu.time", "cpu.mode", "missing"));
        Assert.Null(AttributionMetrics.Value(point, "dotnet.process.cpu.time", "missing-tag", "user"));
    }

    [Fact]
    public void Delta_UsesRawCumulativeUserSystemAndAllocationDifferences()
    {
        var window = new MeasurementWindow(
            SyntheticCapture.CounterPoint(10000, "measure-start", 10, 4, 100 * SyntheticCapture.MiB),
            SyntheticCapture.CounterPoint(20000, "measure-end", 11.5, 4.5, 612 * SyntheticCapture.MiB),
            SyntheticCapture.Frequency, true);

        CostAssert.Equal(1.5, AttributionMetrics.Delta(window, "dotnet.process.cpu.time", "cpu.mode", "user"));
        CostAssert.Equal(0.5, AttributionMetrics.Delta(window, "dotnet.process.cpu.time", "cpu.mode", "system"));
        CostAssert.Equal(2, AttributionMetrics.Delta(window, "dotnet.process.cpu.time"));
        CostAssert.Equal(512 * SyntheticCapture.MiB, AttributionMetrics.Delta(window, "dotnet.gc.heap.total_allocated"));
    }

    [Theory]
    [InlineData(null, 200d)]
    [InlineData(100d, null)]
    [InlineData(null, null)]
    [InlineData(200d, 100d)]
    [InlineData(double.NaN, 200d)]
    [InlineData(100d, double.NaN)]
    public void Delta_MissingResetOrNaNCounterIsUnavailable(double? start, double? end)
    {
        var window = new MeasurementWindow(
            SyntheticCapture.CounterPoint(10000, "measure-start", 10, 4, start),
            SyntheticCapture.CounterPoint(20000, "measure-end", 11.5, 4.5, end),
            SyntheticCapture.Frequency, true);

        Assert.Null(AttributionMetrics.Delta(window, "dotnet.gc.heap.total_allocated"));
    }

    [Fact]
    public void Delta_UnchangedCounterIsMeasuredZero_NotMissing()
    {
        var window = new MeasurementWindow(
            SyntheticCapture.CounterPoint(10000, "measure-start", 10, 4, 100),
            SyntheticCapture.CounterPoint(20000, "measure-end", 10, 4, 100),
            SyntheticCapture.Frequency, true);

        CostAssert.Equal(0, AttributionMetrics.Delta(window, "dotnet.gc.heap.total_allocated"));
        CostAssert.Equal(0, AttributionMetrics.Delta(window, "dotnet.process.cpu.time", "cpu.mode", "user"));
        CostAssert.Equal(0, AttributionMetrics.Delta(window, "dotnet.process.cpu.time", "cpu.mode", "system"));
    }

    [Fact]
    public void Delta_ResetInOneCpuModeCannotBeHiddenByGrowthInTheOther()
    {
        var window = new MeasurementWindow(
            SyntheticCapture.CounterPoint(10000, "measure-start", 10, 4, 100),
            SyntheticCapture.CounterPoint(20000, "measure-end", 9, 6, 200),
            SyntheticCapture.Frequency, true);
        var user = AttributionMetrics.Delta(window, "dotnet.process.cpu.time", "cpu.mode", "user");
        var system = AttributionMetrics.Delta(window, "dotnet.process.cpu.time", "cpu.mode", "system");

        Assert.Null(user);
        CostAssert.Equal(2, system);
        Assert.Null(user + system);
    }
}
