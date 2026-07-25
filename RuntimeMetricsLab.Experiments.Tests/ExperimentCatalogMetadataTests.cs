using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Catalog;

namespace RuntimeMetricsLab.Experiments.Tests;

/// <summary>
/// Verifies internal consistency of the static catalog metadata: every <see cref="ExperimentId"/>
/// is covered exactly once, preset ids are unique per experiment, and every declared
/// <see cref="ParameterBound"/> (including preset values) falls within the hard ceilings in
/// <see cref="ExperimentLimits"/>.
/// </summary>
public sealed class ExperimentCatalogMetadataTests
{
    private static readonly IExperimentCatalog Catalog = new ExperimentCatalog();

    // Maps each named parameter bound to the corresponding hard ceiling, so declared catalog
    // bounds can be asserted to never exceed what the coordinator would otherwise allow.
    private static readonly IReadOnlyDictionary<string, (long Min, long Max)> HardLimits = new Dictionary<string, (long Min, long Max)>
    {
        ["intensityPercent"] = (ExperimentLimits.MinIntensityPercent, ExperimentLimits.MaxIntensityPercent),
        ["workerCount"] = (ExperimentLimits.MinWorkerCount, ExperimentLimits.MaxWorkerCount),
        ["allocationSizeBytes"] = (ExperimentLimits.MinAllocationSizeBytes, ExperimentLimits.MaxAllocationSizeBytes),
        ["allocationsPerSecond"] = (ExperimentLimits.MinAllocationsPerSecond, ExperimentLimits.MaxAllocationsPerSecond),
        ["blockedThreadCount"] = (ExperimentLimits.MinBlockedThreadCount, ExperimentLimits.MaxBlockedThreadCount),
        ["payloadSizeBytes"] = (ExperimentLimits.MinPayloadSizeBytes, ExperimentLimits.MaxPayloadSizeBytes),
        ["delayMilliseconds"] = (ExperimentLimits.MinDelayMilliseconds, ExperimentLimits.MaxDelayMilliseconds),
        ["failureRatePercent"] = (ExperimentLimits.MinFailureRatePercent, ExperimentLimits.MaxFailureRatePercent),
        ["fanOutCount"] = (ExperimentLimits.MinFanOutCount, ExperimentLimits.MaxFanOutCount),
        ["messageCount"] = (ExperimentLimits.MinMessageCount, ExperimentLimits.MaxMessageCount),
        ["messageSizeBytes"] = (ExperimentLimits.MinMessageSizeBytes, ExperimentLimits.MaxMessageSizeBytes),
    };

    [Fact]
    public void Catalog_ContainsEveryExperimentIdExactlyOnce()
    {
        var allIds = Enum.GetValues<ExperimentId>();
        var catalogIds = Catalog.Entries.Select(e => e.Id).ToList();

        Assert.Equal(allIds.Length, catalogIds.Count);
        Assert.Equal(allIds.Length, catalogIds.Distinct().Count());
        foreach (var id in allIds)
        {
            Assert.Contains(id, catalogIds);
        }
    }

    [Fact]
    public void Catalog_TryGetAndGet_AgreeForEveryDeclaredEntry()
    {
        foreach (var entry in Catalog.Entries)
        {
            Assert.True(Catalog.TryGet(entry.Id, out var found));
            Assert.Same(entry, found);
            Assert.Same(entry, Catalog.Get(entry.Id));
        }
    }

    [Fact]
    public void EveryEntry_HasAtLeastOnePreset_WithUniqueIdsAndNonEmptyMetadata()
    {
        foreach (var entry in Catalog.Entries)
        {
            Assert.NotEmpty(entry.Presets);

            var presetIds = entry.Presets.Select(p => p.Id).ToList();
            Assert.Equal(presetIds.Count, presetIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            foreach (var preset in entry.Presets)
            {
                Assert.False(string.IsNullOrWhiteSpace(preset.Id));
                Assert.False(string.IsNullOrWhiteSpace(preset.Name));
                Assert.False(string.IsNullOrWhiteSpace(preset.Description));
            }
        }
    }

    [Fact]
    public void EveryEntry_HasNonEmptyNameDescriptionAndRiskNotes()
    {
        foreach (var entry in Catalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
            Assert.False(string.IsNullOrWhiteSpace(entry.RiskNotes));
        }
    }

    [Fact]
    public void EveryEntry_DurationBound_IsWithinHardLimitsAndInternallyConsistent()
    {
        foreach (var entry in Catalog.Entries)
        {
            Assert.True(entry.Duration.Min <= entry.Duration.Max, $"{entry.Id}: duration min > max.");
            Assert.True(entry.Duration.IsInRange(entry.Duration.Default), $"{entry.Id}: duration default out of its own range.");
            Assert.True(entry.Duration.Min >= ExperimentLimits.MinDurationSeconds, $"{entry.Id}: duration min below hard limit.");
            Assert.True(entry.Duration.Max <= ExperimentLimits.MaxDurationSeconds, $"{entry.Id}: duration max above hard limit.");
        }
    }

    [Fact]
    public void EveryEntry_NamedParameterBounds_AreWithinHardLimitsAndInternallyConsistent()
    {
        foreach (var entry in Catalog.Entries)
        {
            foreach (var (name, bound) in entry.Parameters)
            {
                Assert.True(bound.Min <= bound.Max, $"{entry.Id}.{name}: min > max.");
                Assert.True(bound.IsInRange(bound.Default), $"{entry.Id}.{name}: default out of its own range.");

                Assert.True(HardLimits.TryGetValue(name, out var limit), $"{entry.Id}.{name}: no known hard limit mapping for this parameter name.");
                Assert.True(bound.Min >= limit.Min, $"{entry.Id}.{name}: declared min {bound.Min} is below hard limit {limit.Min}.");
                Assert.True(bound.Max <= limit.Max, $"{entry.Id}.{name}: declared max {bound.Max} exceeds hard limit {limit.Max}.");
            }
        }
    }

    [Fact]
    public void EveryPreset_DurationValue_WhenSet_IsWithinItsEntrysDurationBound()
    {
        foreach (var entry in Catalog.Entries)
        {
            foreach (var preset in entry.Presets)
            {
                if (preset.Parameters.DurationSeconds is { } duration)
                {
                    Assert.True(entry.Duration.IsInRange(duration), $"{entry.Id}/{preset.Id}: preset duration {duration} out of range.");
                }
            }
        }
    }
}
