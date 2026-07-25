using RuntimeMetricsLab.Contracts.Catalog;

namespace RuntimeMetricsLab.Experiments.Catalog;

/// <summary>Read-only metadata store describing every experiment the lab can run.</summary>
public interface IExperimentCatalog
{
    IReadOnlyList<ExperimentCatalogEntry> Entries { get; }

    bool TryGet(ExperimentId id, out ExperimentCatalogEntry entry);

    ExperimentCatalogEntry Get(ExperimentId id);
}
