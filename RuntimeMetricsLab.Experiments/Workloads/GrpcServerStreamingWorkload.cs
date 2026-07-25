using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Abstractions;

namespace RuntimeMetricsLab.Experiments.Workloads;

/// <summary>Drives one server-streaming gRPC call against this service's own gRPC endpoint via the injected invoker.</summary>
public sealed class GrpcServerStreamingWorkload(IGrpcWorkloadInvoker invoker) : IExperimentWorkload
{
    public ExperimentId Id => ExperimentId.GrpcServerStreaming;

    public Task ExecuteAsync(ExperimentRunContext context, CancellationToken cancellationToken)
    {
        var messageCount = Math.Max(1, context.Parameters.MessageCount ?? 50);
        var messageSizeBytes = Math.Max(1, context.Parameters.MessageSizeBytes ?? 4 * 1024);

        return invoker.InvokeServerStreamingAsync(messageCount, messageSizeBytes, context.Metrics, cancellationToken);
    }
}
