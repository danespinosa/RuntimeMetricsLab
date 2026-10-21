using RuntimeMetricsLab.Analysis;

namespace RuntimeMetricsLab.Attribution.Tests;

public sealed class AllocationEstimatorTests
{
    [Theory]
    [InlineData(24L, 102411.500467938482)]
    [InlineData(104L, 102451.508801312355)]
    [InlineData(1024L, 102912.353335263981)]
    [InlineData(65560L, 138653.665482244946)]
    [InlineData(262168L, 284126.937334815557)]
    [InlineData(1048600L, 1048637.439182857472)]
    public void EstimatedBytes_MatchesIndependentRandomizedSamplingReferences(long objectSize, double expected)
    {
        var actual = AllocationEstimator.EstimatedBytes(objectSize);

        Assert.InRange(Math.Abs(actual - expected), 0, 0.000001);
        Assert.True(actual >= objectSize);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void EstimatedBytes_NonpositiveObjectSizeIsRejected(long objectSize)
    {
        Assert.Throws<InvalidDataException>(() => AllocationEstimator.EstimatedBytes(objectSize));
    }

    [Fact]
    public void EstimatedBytes_LargeObjectsApproachTheirOwnSizeRatherThanOneSamplingInterval()
    {
        Assert.Equal(8 * 1024 * 1024d, AllocationEstimator.EstimatedBytes(8 * 1024 * 1024));
    }

    [Fact]
    public void AllocationRateAndPerCall_UseBytesNotCpuShare()
    {
        const double bytes = 512 * 1024 * 1024;

        Assert.Equal(51.2, bytes / 10 / (1024 * 1024), 10);
        CostAssert.Equal(128 * 1024, AttributionMath.PerCall(bytes, 4096, 4096, true));
    }
}
