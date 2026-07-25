using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Contracts.Runs;

namespace RuntimeMetricsLab.Experiments.Coordination;

/// <summary>
/// Resolves a <see cref="StartExperimentRequest"/> into a fully-populated <see cref="ExperimentParameters"/>
/// (catalog defaults &lt; preset &lt; explicit overrides) and validates the result against the
/// experiment's declared bounds. Every numeric field the catalog exposes for an experiment is
/// validated; fields the experiment doesn't use are ignored even if present on the request.
/// </summary>
internal static class ExperimentParameterResolver
{
    public static ExperimentParameters Resolve(ExperimentCatalogEntry entry, StartExperimentRequest request)
    {
        var resolved = ApplyDefaults(entry);

        if (request.PresetId is not null)
        {
            var preset = entry.Presets.FirstOrDefault(p => string.Equals(p.Id, request.PresetId, StringComparison.OrdinalIgnoreCase))
                ?? throw new PresetNotFoundException(entry.Id, request.PresetId);
            resolved = Merge(resolved, preset.Parameters);
        }

        if (request.Parameters is not null)
        {
            resolved = Merge(resolved, request.Parameters);
        }

        var errors = Validate(entry, resolved);
        if (errors.Count > 0)
        {
            throw new ExperimentValidationException(entry.Id, errors);
        }

        return resolved;
    }

    private static ExperimentParameters ApplyDefaults(ExperimentCatalogEntry entry)
    {
        var parameters = new ExperimentParameters { DurationSeconds = entry.Duration.Default };
        foreach (var (name, bound) in entry.Parameters)
        {
            parameters = WithValue(parameters, name, bound.Default);
        }
        return parameters;
    }

    private static ExperimentParameters Merge(ExperimentParameters baseline, ExperimentParameters overlay) => baseline with
    {
        DurationSeconds = overlay.DurationSeconds ?? baseline.DurationSeconds,
        IntensityPercent = overlay.IntensityPercent ?? baseline.IntensityPercent,
        WorkerCount = overlay.WorkerCount ?? baseline.WorkerCount,
        AllocationSizeBytes = overlay.AllocationSizeBytes ?? baseline.AllocationSizeBytes,
        AllocationsPerSecond = overlay.AllocationsPerSecond ?? baseline.AllocationsPerSecond,
        BlockedThreadCount = overlay.BlockedThreadCount ?? baseline.BlockedThreadCount,
        PayloadSizeBytes = overlay.PayloadSizeBytes ?? baseline.PayloadSizeBytes,
        DelayMilliseconds = overlay.DelayMilliseconds ?? baseline.DelayMilliseconds,
        FailureRatePercent = overlay.FailureRatePercent ?? baseline.FailureRatePercent,
        FanOutCount = overlay.FanOutCount ?? baseline.FanOutCount,
        MessageCount = overlay.MessageCount ?? baseline.MessageCount,
        MessageSizeBytes = overlay.MessageSizeBytes ?? baseline.MessageSizeBytes,
    };

    private static ExperimentParameters WithValue(ExperimentParameters parameters, string name, int value) => name switch
    {
        "intensityPercent" => parameters with { IntensityPercent = value },
        "workerCount" => parameters with { WorkerCount = value },
        "allocationSizeBytes" => parameters with { AllocationSizeBytes = value },
        "allocationsPerSecond" => parameters with { AllocationsPerSecond = value },
        "blockedThreadCount" => parameters with { BlockedThreadCount = value },
        "payloadSizeBytes" => parameters with { PayloadSizeBytes = value },
        "delayMilliseconds" => parameters with { DelayMilliseconds = value },
        "failureRatePercent" => parameters with { FailureRatePercent = value },
        "fanOutCount" => parameters with { FanOutCount = value },
        "messageCount" => parameters with { MessageCount = value },
        "messageSizeBytes" => parameters with { MessageSizeBytes = value },
        _ => throw new InvalidOperationException($"Unknown parameter bound name '{name}'."),
    };

    private static long? ReadValue(ExperimentParameters parameters, string name) => name switch
    {
        "intensityPercent" => parameters.IntensityPercent,
        "workerCount" => parameters.WorkerCount,
        "allocationSizeBytes" => parameters.AllocationSizeBytes,
        "allocationsPerSecond" => parameters.AllocationsPerSecond,
        "blockedThreadCount" => parameters.BlockedThreadCount,
        "payloadSizeBytes" => parameters.PayloadSizeBytes,
        "delayMilliseconds" => parameters.DelayMilliseconds,
        "failureRatePercent" => parameters.FailureRatePercent,
        "fanOutCount" => parameters.FanOutCount,
        "messageCount" => parameters.MessageCount,
        "messageSizeBytes" => parameters.MessageSizeBytes,
        _ => throw new InvalidOperationException($"Unknown parameter bound name '{name}'."),
    };

    private static List<string> Validate(ExperimentCatalogEntry entry, ExperimentParameters parameters)
    {
        var errors = new List<string>();

        var duration = parameters.DurationSeconds ?? entry.Duration.Default;
        if (!entry.Duration.IsInRange(duration))
        {
            errors.Add($"durationSeconds must be between {entry.Duration.Min} and {entry.Duration.Max} (got {duration}).");
        }

        foreach (var (name, bound) in entry.Parameters)
        {
            var value = ReadValue(parameters, name) ?? bound.Default;
            if (value < bound.Min || value > bound.Max)
            {
                errors.Add($"{name} must be between {bound.Min} and {bound.Max} (got {value}).");
            }
        }

        return errors;
    }
}
