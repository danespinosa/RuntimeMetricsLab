using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Collector;

public interface ICollectorControl
{
    Task<CapabilityStatus> Capabilities(CancellationToken token);
    Task<CollectorStatus> Start(CollectorStartRequest request, CancellationToken token);
    Task<CollectorStatus> Get(Guid id, CancellationToken token);
    Task<CollectorStatus?> Find(Guid operationId, CancellationToken token);
    Task<CollectorStatus> Stop(Guid id, bool cancelled, CancellationToken token);
}

public sealed class LinuxCollectorControl(CaptureManager manager) : ICollectorControl
{
    public Task<CapabilityStatus> Capabilities(CancellationToken token) => Task.FromResult(manager.Capabilities());
    public Task<CollectorStatus> Start(CollectorStartRequest request, CancellationToken token) => Task.FromResult(manager.Start(request));
    public Task<CollectorStatus> Get(Guid id, CancellationToken token) => Task.FromResult(manager.Get(id));
    public Task<CollectorStatus?> Find(Guid operationId, CancellationToken token) => Task.FromResult(manager.FindOperation(operationId));
    public Task<CollectorStatus> Stop(Guid id, bool cancelled, CancellationToken token) => Task.FromResult(manager.Stop(id, cancelled));
}
