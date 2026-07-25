using RuntimeMetricsLab.Experiments.Metrics;

namespace RuntimeMetricsLab.Experiments.Abstractions;

/// <summary>
/// Drives gRPC calls against this service's own gRPC endpoint. Implemented in
/// RuntimeMetricsLab.ApiService (the only project allowed to reference gRPC packages / generated
/// proto code); kept as an abstraction here so the Experiments project stays free of gRPC
/// dependencies.
/// </summary>
public interface IGrpcWorkloadInvoker
{
    /// <summary>Issues <paramref name="messageCount"/> sequential unary calls, each echoing a payload of <paramref name="messageSizeBytes"/>.</summary>
    Task InvokeUnaryAsync(int messageCount, int messageSizeBytes, IExperimentMetricsRecorder recorder, CancellationToken cancellationToken);

    /// <summary>Opens one server-streaming call and consumes <paramref name="messageCount"/> messages of <paramref name="messageSizeBytes"/>.</summary>
    Task InvokeServerStreamingAsync(int messageCount, int messageSizeBytes, IExperimentMetricsRecorder recorder, CancellationToken cancellationToken);
}
