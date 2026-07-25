using System.Text.Json.Serialization;

namespace RuntimeMetricsLab.Contracts.Catalog;

/// <summary>Broad grouping used for catalog navigation and low-cardinality metric tagging.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExperimentCategory>))]
public enum ExperimentCategory
{
    Cpu,
    Memory,
    ThreadPool,
    Http,
    Sockets,
    Grpc,
    CustomMetrics,
}
