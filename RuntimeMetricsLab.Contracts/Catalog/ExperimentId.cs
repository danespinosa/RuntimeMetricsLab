using System.Text.Json.Serialization;

namespace RuntimeMetricsLab.Contracts.Catalog;

/// <summary>
/// Stable identifiers for every experiment exposed by the lab. Values are serialized as
/// strings so the wire contract stays low-cardinality and human readable.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExperimentId>))]
public enum ExperimentId
{
    CpuBurn,
    MemoryAllocationChurn,
    ThreadPoolStarvation,
    HttpPayloadStress,
    HttpArtificialDelay,
    HttpFailureInjection,
    HttpFanOut,
    TlsHandshakeStress,
    GrpcUnaryEcho,
    GrpcServerStreaming,
    CustomOtelMetricsDemo,
}
