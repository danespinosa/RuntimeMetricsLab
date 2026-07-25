using Grpc.Core;
using Grpc.Net.Client;
using RuntimeMetricsLab.ApiService.Grpc;
using RuntimeMetricsLab.Experiments.Abstractions;
using RuntimeMetricsLab.Experiments.Metrics;

namespace RuntimeMetricsLab.ApiService.Grpc;

/// <summary>
/// Drives the gRPC lab experiments by calling <see cref="WorkloadGrpcService"/> on this same
/// process over a loopback <see cref="GrpcChannel"/>. Kept out of RuntimeMetricsLab.Experiments so
/// that project never needs to reference gRPC packages or generated proto code.
/// </summary>
public sealed class GrpcWorkloadInvoker : IGrpcWorkloadInvoker, IDisposable
{
    private readonly ILoopbackHttpClientProvider _loopback;
    private readonly Lazy<GrpcChannel> _channel;

    public GrpcWorkloadInvoker(ILoopbackHttpClientProvider loopback)
    {
        _loopback = loopback;
        _channel = new Lazy<GrpcChannel>(CreateChannel);
    }

    private GrpcChannel CreateChannel() => GrpcChannel.ForAddress(_loopback.BaseAddress, new GrpcChannelOptions
    {
        HttpClient = _loopback.GetPooledClient(),
    });

    public async Task InvokeUnaryAsync(int messageCount, int messageSizeBytes, IExperimentMetricsRecorder recorder, CancellationToken cancellationToken)
    {
        var client = new WorkloadService.WorkloadServiceClient(_channel.Value);

        for (var i = 0; i < messageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await client.UnaryEchoAsync(
                    new EchoRequest { PayloadSizeBytes = messageSizeBytes },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                recorder.GrpcMessage("unary");
            }
            catch (RpcException) when (!cancellationToken.IsCancellationRequested)
            {
                recorder.GrpcMessage("unary_error");
            }

            recorder.WorkIterations();
        }
    }

    public async Task InvokeServerStreamingAsync(int messageCount, int messageSizeBytes, IExperimentMetricsRecorder recorder, CancellationToken cancellationToken)
    {
        var client = new WorkloadService.WorkloadServiceClient(_channel.Value);

        using var call = client.StreamEcho(
            new StreamRequest { PayloadSizeBytes = messageSizeBytes, MessageCount = messageCount },
            cancellationToken: cancellationToken);

        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            recorder.GrpcMessage("stream");
            recorder.WorkIterations();
        }
    }

    public void Dispose()
    {
        if (_channel.IsValueCreated)
        {
            _channel.Value.Dispose();
        }
    }
}
