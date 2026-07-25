using RuntimeMetricsLab.Contracts.Catalog;
using RuntimeMetricsLab.Experiments.Abstractions;

namespace RuntimeMetricsLab.Experiments.Workloads;

/// <summary>Drives repeated unary gRPC calls against this service's own gRPC endpoint via the injected invoker.</summary>
public sealed class GrpcUnaryEchoWorkload(IGrpcWorkloadInvoker invoker) : IExperimentWorkload
{
    public ExperimentId Id => ExperimentId.GrpcUnaryEcho;

    public Task ExecuteAsync(ExperimentRunContext context, CancellationToken cancellationToken)
    {
        var messageCount = Math.Max(1, context.Parameters.MessageCount ?? 50);
        var messageSizeBytes = Math.Max(1, context.Parameters.MessageSizeBytes ?? 4 * 1024);

        return invoker.InvokeUnaryAsync(messageCount, messageSizeBytes, context.Metrics, cancellationToken);
    }
}
