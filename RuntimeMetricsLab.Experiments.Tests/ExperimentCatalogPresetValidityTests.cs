using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Contracts.Runs;
using RuntimeMetricsLab.Experiments.Tests.Support;

namespace RuntimeMetricsLab.Experiments.Tests;

/// <summary>
/// Black-box check, through the public coordinator API, that every declared preset for every
/// experiment actually resolves and validates successfully. Complements
/// <see cref="ExperimentCatalogMetadataTests"/> (which inspects raw metadata) by exercising the
/// real resolve-then-validate path (<c>ExperimentParameterResolver</c>) that a live API request
/// would take.
/// </summary>
public sealed class ExperimentCatalogPresetValidityTests
{
    [Fact]
    public async Task EveryPreset_ForEveryExperiment_StartsSuccessfullyWithoutValidationErrors()
    {
        using var harness = new TestCoordinatorHarness();

        foreach (var entry in harness.Catalog.Entries)
        {
            harness.WorkloadFor(entry.Id).Execute = static async (_, _) => await Task.Yield();

            foreach (var preset in entry.Presets)
            {
                // Start() itself throws (PresetNotFoundException/ExperimentValidationException) if
                // the preset fails to resolve/validate, so simply not throwing here is the
                // meaningful assertion for this test.
                harness.Coordinator.Start(entry.Id, new StartExperimentRequest(preset.Id, null));

                // Cooldown is zero in this harness, so waiting for a terminal status is enough to
                // let the next preset for this same experiment start without hitting the
                // one-active-run guard.
                var snapshot = await harness.WaitForStatusAsync(entry.Id, s => s != ExperimentRunStatus.Running);
                Assert.Equal(ExperimentRunStatus.Completed, snapshot.Status);
            }
        }
    }
}
