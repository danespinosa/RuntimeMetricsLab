using Grpc.Core;

namespace RuntimeMetricsLab.ApiService.Grpc;

/// <summary>
/// Loopback-only gRPC service consumed by <c>GrpcUnaryEchoWorkload</c> and
/// <c>GrpcServerStreamingWorkload</c>. It exists purely to generate real gRPC wire traffic for the
/// lab experiments; it performs no other business logic.
/// </summary>
public sealed class WorkloadGrpcService : WorkloadService.WorkloadServiceBase
{
    public override Task<EchoReply> UnaryEcho(EchoRequest request, ServerCallContext context)
    {
        var payload = CreatePayload(request.PayloadSizeBytes);
        return Task.FromResult(new EchoReply { Sequence = 0, Payload = Google.Protobuf.ByteString.CopyFrom(payload) });
    }

    public override async Task StreamEcho(StreamRequest request, IServerStreamWriter<EchoReply> responseStream, ServerCallContext context)
    {
        var payload = CreatePayload(request.PayloadSizeBytes);
        var messageCount = Math.Max(1, request.MessageCount);

        for (var sequence = 0; sequence < messageCount; sequence++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            await responseStream.WriteAsync(new EchoReply { Sequence = sequence, Payload = Google.Protobuf.ByteString.CopyFrom(payload) }).ConfigureAwait(false);
        }
    }

    private static byte[] CreatePayload(int sizeBytes)
    {
        var size = Math.Clamp(sizeBytes, 1, 4 * 1024 * 1024);
        return new byte[size];
    }
}
