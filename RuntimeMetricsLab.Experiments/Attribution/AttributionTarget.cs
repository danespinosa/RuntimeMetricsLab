using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RuntimeMetricsLab.Contracts.Attribution;
using RuntimeMetricsLab.Experiments.Diagnostics;

namespace RuntimeMetricsLab.Experiments.Attribution;

public sealed class AttributionTarget(RuntimeSampler sampler, ILogger<AttributionTarget> logger,
    IAttributionTargetEnvironment environment) : IDisposable
{
    private readonly object _gate = new();
    private readonly List<byte[]> _retained = new();
    private readonly TargetIdentity _identity = environment.ReadIdentity();
    private Guid? _runId;
    private LessonSettings? _settings;
    private string _phase = "idle";
    private string? _error;
    private TargetMeasurement? _measurement;
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private bool? _profiledRun;
    private bool _leased;
    private DateTimeOffset _expires;
    private long _allocationRemaining;
    private long _retentionRemaining;

    public TargetStatus Status()
    {
        lock (_gate)
            return new(_runId, _phase, AttributionMarkers.Log.IsEnabled(), _identity, _measurement, _error);
    }

    public TargetStatus Prepare(PrepareTargetRequest request)
    {
        AttributionLessons.Validate(request.Settings);
        if (request.RunId == Guid.Empty)
            throw new ArgumentException("A non-empty server-generated run identity is required.");
        lock (_gate)
        {
            if (_runId == request.RunId && _settings == request.Settings)
                return Status();
            if (_leased && (_expires > DateTimeOffset.UtcNow || _task is { IsCompleted: false }))
                throw new InvalidOperationException("The target is reserved by another capture.");
            _runId = request.RunId;
            _settings = request.Settings;
            _measurement = null;
            _error = null;
            _task = null;
            _profiledRun = null;
            _leased = true;
            _phase = "prepared";
            _expires = DateTimeOffset.UtcNow.AddSeconds(request.Settings.DurationSeconds + 60);
            _cancellation?.Dispose();
            _cancellation = new(TimeSpan.FromSeconds(request.Settings.DurationSeconds + 45));
            _retained.Clear();
            _allocationRemaining = request.Settings.AllocationMiB * 1024L * 1024;
            _retentionRemaining = request.Settings.RetainedMiB * 1024L * 1024;
            var warmup = new AttributionLessons.Counts();
            AttributionLessons.Checksum(warmup, AttributionLessons.Arithmetic(warmup, 1));
            GC.KeepAlive(AttributionLessons.AllocateSmall(warmup));
            GC.KeepAlive(AttributionLessons.AllocateLarge(warmup));
            GC.KeepAlive(AttributionLessons.RetainPayload(warmup));
            AttributionLessons.MixedPipeline(warmup, 1, true);
            return Status();
        }
    }

    public TargetStatus Run(Guid id) => StartRun(id, true);

    public TargetStatus RunCounterDiagnostic(Guid id)
    {
        if (_identity.Platform != AttributionPlatform.WindowsProcess)
            throw new InvalidOperationException("Counter-only diagnostics are an explicit native Windows option.");
        return StartRun(id, false);
    }

    private TargetStatus StartRun(Guid id, bool profiled)
    {
        lock (_gate)
        {
            RequireLease(id);
            if (_profiledRun.HasValue && _profiledRun != profiled)
                throw new InvalidOperationException("A run identity cannot change collection kind.");
            _profiledRun = profiled;
            _task ??= Task.Run(() => MeasureAsync(id, _settings!, profiled, _cancellation!.Token));
            return Status();
        }
    }

    public TargetStatus Cancel(Guid id)
    {
        lock (_gate)
        {
            RequireLease(id);
            _cancellation!.Cancel();
            if (_task is null)
                _phase = "cancelled";
            return Status();
        }
    }

    public async Task ReleaseAsync(Guid id)
    {
        Task? task;
        lock (_gate)
        {
            if (_runId == id && !_leased) return;
            RequireLease(id);
            task = _task;
        }
        if (task is not null)
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (_gate)
        {
            if (_runId == id && !_leased) return;
            RequireLease(id);
            _leased = false;
        }
    }

    private async Task MeasureAsync(Guid id, LessonSettings settings, bool profiled, CancellationToken token)
    {
        RuntimePoint? start = null;
        RuntimePoint? end = null;
        var counters = Enumerable.Range(0, settings.Workers).Select(_ => new AttributionLessons.Counts()).ToArray();
        using var process = Process.GetCurrentProcess();
        var cpuStart = TimeSpan.Zero;
        double independentCpu = 0;
        long independentBytes = 0;
        long allocatedStart = 0, historyStart = 0, retainedPayload = 0, checksum = 0;
        var complete = false;
        try
        {
            if (profiled)
            {
                SetPhase("attaching");
                var deadline = Stopwatch.StartNew();
                while (!AttributionMarkers.Log.IsEnabled())
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(AttributionBudgets.AttachmentSeconds))
                        throw new TimeoutException("Collector did not enable the lab marker provider. No workload was measured.");
                    await Task.Delay(50, token);
                }
                Mark(id, "attached");
                AttributionMarkers.Log.Identity(id.ToString("N"), _identity.InstanceId.ToString("N"),
                    _identity.ProcessId, _identity.ProcessStartUtc?.ToString("O") ?? "",
                    _identity.Environment.GetValueOrDefault("workload-module-mvid") ?? "");
            }
            SetPhase("baseline");
            historyStart = sampler.Sample("baseline").MonotonicTicks;
            await Task.Delay(1000, token);
            using var ready = new CountdownEvent(settings.Workers);
            using var go = new ManualResetEventSlim();
            long windowStart = 0;
            var workers = counters.Select((counts, index) => Task.Factory.StartNew(() =>
            {
                ready.Signal();
                go.Wait();
                long result = index, iteration = 0;
                while (!token.IsCancellationRequested && Stopwatch.GetElapsedTime(windowStart).TotalSeconds < settings.DurationSeconds)
                {
                    switch (settings.Lesson)
                    {
                        case "cpu":
                            result = AttributionLessons.Checksum(counts, AttributionLessons.Arithmetic(counts, result));
                            break;
                        case "allocation":
                            var large = iteration % 16 == 0;
                            if (Reserve(ref _allocationRemaining, large ? 128 * 1024 : 4096))
                                GC.KeepAlive(large ? AttributionLessons.AllocateLarge(counts) : AttributionLessons.AllocateSmall(counts));
                            Thread.Sleep(1);
                            break;
                        case "retention":
                            if (Reserve(ref _retentionRemaining, 256 * 1024) && Reserve(ref _allocationRemaining, 256 * 1024))
                            {
                                var payload = AttributionLessons.RetainPayload(counts);
                                lock (_retained) _retained.Add(payload);
                                Interlocked.Add(ref retainedPayload, payload.Length);
                            }
                            Thread.Sleep(2);
                            break;
                        case "mixed":
                            result = AttributionLessons.MixedPipeline(counts, result, Reserve(ref _allocationRemaining, 4096));
                            break;
                    }
                    iteration++;
                }
                lock (counters) checksum ^= result;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            try
            {
                ready.Wait(token);
                SetPhase("measuring");
                cpuStart = process.TotalProcessorTime;
                allocatedStart = GC.GetTotalAllocatedBytes(true);
                start = sampler.Sample("measure-start");
                windowStart = start.MonotonicTicks;
                AttributionMarkers.Log.Boundary(id.ToString("N"), "measure-start", windowStart);
            }
            finally
            {
                go.Set();
                await Task.WhenAll(workers);
            }
            end = sampler.Sample("measure-end");
            AttributionMarkers.Log.Boundary(id.ToString("N"), "measure-end", end.MonotonicTicks);
            complete = !token.IsCancellationRequested;
            independentCpu = (process.TotalProcessorTime - cpuStart).TotalSeconds;
            independentBytes = GC.GetTotalAllocatedBytes(true) - allocatedStart;
            SetPhase("recovery");
            if (settings.Lesson == "retention")
            {
                await Task.Delay(1000, token);
                Mark(id, "release-retained-payload");
                lock (_retained) _retained.Clear();
                SetPhase("explicit-gc-after-release");
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            await Task.Delay(1000, token);
            lock (_gate)
            {
                _measurement = BuildMeasurement(id, settings, start, end, counters, historyStart,
                    retainedPayload, independentCpu, independentBytes, checksum, complete, null);
                _phase = complete ? "completed" : "cancelled";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_gate)
            {
                _phase = "cancelled";
                _error = "Cancelled or the finite target deadline expired; normal per-call estimates are unavailable.";
                if (start is not null && end is not null)
                    _measurement = BuildMeasurement(id, settings, start, end, counters, historyStart,
                        retainedPayload, independentCpu, independentBytes, checksum, false, _error);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Attribution target run {RunId} failed", id);
            lock (_gate) { _phase = "failed"; _error = exception.Message; }
        }
        finally
        {
            lock (_retained) _retained.Clear();
            sampler.Phase = "idle";
        }
    }

    private TargetMeasurement BuildMeasurement(Guid id, LessonSettings settings, RuntimePoint start, RuntimePoint end,
        AttributionLessons.Counts[] counts, long historyStart, long retained, double cpu, long allocated, long checksum, bool complete, string? error) =>
        new(id, _identity, settings, new(start, end, Stopwatch.Frequency, complete),
            AttributionLessons.MethodNames.Select((name, index) => new MethodCalls(name,
                counts.Sum(count => count.Entered[index]), counts.Sum(count => count.Completed[index]),
                counts.Sum(count => count.Allocated[index]))).ToArray(),
            sampler.Since(historyStart), retained, cpu, allocated, checksum, error);

    private void SetPhase(string phase) { lock (_gate) _phase = phase; sampler.Phase = phase; }
    private static void Mark(Guid id, string phase) => AttributionMarkers.Log.Boundary(id.ToString("N"), phase, Stopwatch.GetTimestamp());
    private void RequireLease(Guid id)
    {
        if (!_leased || _runId != id)
            throw new InvalidOperationException("No matching target-wide attribution lease.");
    }

    private static bool Reserve(ref long remaining, int bytes)
    {
        long current;
        do
        {
            current = Volatile.Read(ref remaining);
            if (current < bytes) return false;
        } while (Interlocked.CompareExchange(ref remaining, current - bytes, current) != current);
        return true;
    }

    public void Dispose() { _cancellation?.Cancel(); _cancellation?.Dispose(); }
}
