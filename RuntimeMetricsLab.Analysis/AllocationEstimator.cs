namespace RuntimeMetricsLab.Analysis;

public static class AllocationEstimator
{
    // .NET 10 randomized sampling: geometric byte sampling with a 100 KiB mean interval.
    public static double EstimatedBytes(long objectSize)
    {
        if (objectSize <= 0)
            throw new InvalidDataException("AllocationSampled requires a positive ObjectSize.");
        return objectSize / (1 - Math.Pow(1 - 1.0 / 102400, objectSize));
    }
}
