namespace RuntimeMetricsLab.Experiments.Catalog;

/// <summary>
/// Hard ceilings enforced by the coordinator regardless of what a catalog entry declares.
/// These exist so a bug in catalog metadata can never let a client request something that would
/// meaningfully harm the host process or the machine it runs on. Catalog-declared
/// <see cref="Contracts.Catalog.ParameterBound"/> values must always fall within these limits;
/// this is asserted by <c>ExperimentCatalogTests</c>.
/// </summary>
public static class ExperimentLimits
{
    public const int MinDurationSeconds = 1;
    public const int MaxDurationSeconds = 60;

    public const int MinIntensityPercent = 1;
    public const int MaxIntensityPercent = 100;

    public const int MinWorkerCount = 1;
    public static readonly int MaxWorkerCount = Math.Max(4, Environment.ProcessorCount * 2);

    public const long MinAllocationSizeBytes = 1_024;
    public const long MaxAllocationSizeBytes = 64L * 1024 * 1024;

    public const int MinAllocationsPerSecond = 1;
    public const int MaxAllocationsPerSecond = 2_000;

    public const int MinBlockedThreadCount = 1;
    public const int MaxBlockedThreadCount = 100;

    public const int MinPayloadSizeBytes = 1;
    public const int MaxPayloadSizeBytes = 16 * 1024 * 1024;

    public const int MinDelayMilliseconds = 0;
    public const int MaxDelayMilliseconds = 10_000;

    public const int MinFailureRatePercent = 0;
    public const int MaxFailureRatePercent = 100;

    public const int MinFanOutCount = 1;
    public const int MaxFanOutCount = 50;

    public const int MinMessageCount = 1;
    public const int MaxMessageCount = 1_000;

    public const int MinMessageSizeBytes = 1;
    public const int MaxMessageSizeBytes = 4 * 1024 * 1024;

    /// <summary>Minimum time an experiment must wait after finishing before it can be started again.</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(5);
}
