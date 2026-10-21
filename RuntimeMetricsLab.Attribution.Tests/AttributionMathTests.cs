using RuntimeMetricsLab.Analysis;

namespace RuntimeMetricsLab.Attribution.Tests;

public sealed class AttributionMathTests
{
    [Theory]
    [InlineData(2, 4, 0.5)]
    [InlineData(40, 10, 4)]
    [InlineData(1, 4, 0.25)]
    [InlineData(10, 4, 2.5)]
    [InlineData(0, 4, 0)]
    public void AverageCores_UsesCpuSecondsOverTheSameElapsedWindow(double cpuSeconds, double seconds, double expected)
    {
        CostAssert.Equal(expected, AttributionMath.AverageCores(cpuSeconds, seconds));
    }

    [Theory]
    [InlineData(null, 4)]
    [InlineData(-1d, 4)]
    [InlineData(double.NaN, 4)]
    [InlineData(double.PositiveInfinity, 4)]
    [InlineData(double.NegativeInfinity, 4)]
    [InlineData(2d, 0)]
    [InlineData(2d, -1)]
    [InlineData(2d, double.NaN)]
    [InlineData(2d, double.PositiveInfinity)]
    [InlineData(2d, double.NegativeInfinity)]
    public void AverageCores_UnavailableOrInvalidInputsAreNotZero(double? cpuSeconds, double seconds)
    {
        Assert.Null(AttributionMath.AverageCores(cpuSeconds, seconds));
    }

    [Theory]
    [InlineData(2, 75, 100, 1500)]
    [InlineData(2, 25, 100, 500)]
    [InlineData(0.5, 3, 4, 375)]
    [InlineData(40, 25, 100, 10000)]
    [InlineData(2, 0, 100, 0)]
    [InlineData(0, 75, 100, 0)]
    public void CpuMilliseconds_UsesSampleShareOfCoveredCpu(double coveredSeconds, double weight, double totalWeight,
        double expected)
    {
        CostAssert.Equal(expected, AttributionMath.CpuMilliseconds(coveredSeconds, weight, totalWeight));
    }

    [Theory]
    [InlineData(null, 1, 4)]
    [InlineData(-1d, 1, 4)]
    [InlineData(double.NaN, 1, 4)]
    [InlineData(double.PositiveInfinity, 1, 4)]
    [InlineData(double.NegativeInfinity, 1, 4)]
    [InlineData(2d, -1, 4)]
    [InlineData(2d, double.NaN, 4)]
    [InlineData(2d, double.PositiveInfinity, 4)]
    [InlineData(2d, 5, 4)]
    [InlineData(2d, 0, 0)]
    [InlineData(2d, 1, -1)]
    [InlineData(2d, 1, double.NaN)]
    [InlineData(2d, 1, double.PositiveInfinity)]
    public void CpuMilliseconds_InvalidPoolsAndWeightsAreUnavailable(double? coveredSeconds, double weight,
        double totalWeight)
    {
        Assert.Null(AttributionMath.CpuMilliseconds(coveredSeconds, weight, totalWeight));
    }

    [Fact]
    public void CpuMilliseconds_PartialCoveredPoolDoesNotExpandToAllProcessCpu()
    {
        var processCores = AttributionMath.AverageCores(8, 4);
        var first = AttributionMath.CpuMilliseconds(2, 75, 100);
        var second = AttributionMath.CpuMilliseconds(2, 25, 100);
        var coveredCores = AttributionMath.AverageCores((first + second) / 1000, 4);

        CostAssert.Equal(1500, first);
        CostAssert.Equal(500, second);
        CostAssert.Equal(0.5, coveredCores);
        CostAssert.Equal(1.5, processCores - coveredCores);
    }

    [Fact]
    public void CpuMilliseconds_PartialSampleSharesLeaveTheRestUnassigned()
    {
        var namedMethods = AttributionMath.CpuMilliseconds(2, 50, 100) +
            AttributionMath.CpuMilliseconds(2, 25, 100);

        CostAssert.Equal(1500, namedMethods);
        CostAssert.Equal(500, 2000 - namedMethods);
    }

    [Fact]
    public void QuarterOfFourCoreProcess_IsOneCore_NotQuarterOfOneCore()
    {
        var methodMilliseconds = AttributionMath.CpuMilliseconds(40, 25, 100);
        var methodCores = AttributionMath.AverageCores(methodMilliseconds / 1000, 10);

        CostAssert.Equal(4, AttributionMath.AverageCores(40, 10));
        CostAssert.Equal(1, methodCores);
        CostAssert.Equal(25, methodCores * 100 / 4);
    }

    [Theory]
    [InlineData(1500, 300, 5)]
    [InlineData(500, 100, 5)]
    [InlineData(0, 100, 0)]
    [InlineData(536870912, 4096, 131072)]
    public void PerCall_UsesCompletedAlignedCounts(double cost, long calls, double expected)
    {
        CostAssert.Equal(expected, AttributionMath.PerCall(cost, calls, calls, true));
    }

    [Theory]
    [InlineData(null, 100L, 100L, true)]
    [InlineData(-1d, 100L, 100L, true)]
    [InlineData(double.NaN, 100L, 100L, true)]
    [InlineData(double.PositiveInfinity, 100L, 100L, true)]
    [InlineData(double.NegativeInfinity, 100L, 100L, true)]
    [InlineData(500d, null, null, true)]
    [InlineData(500d, null, 100L, true)]
    [InlineData(500d, 100L, null, true)]
    [InlineData(500d, 0L, 0L, true)]
    [InlineData(500d, -1L, -1L, true)]
    [InlineData(500d, 101L, 100L, true)]
    [InlineData(500d, 99L, 100L, true)]
    [InlineData(500d, 100L, 100L, false)]
    public void PerCall_InvalidCostCountsOrWindowAreUnavailable(double? cost, long? entered, long? completed,
        bool aligned)
    {
        Assert.Null(AttributionMath.PerCall(cost, entered, completed, aligned));
    }
}
