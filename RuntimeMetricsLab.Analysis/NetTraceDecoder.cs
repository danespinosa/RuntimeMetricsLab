using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using System.Globalization;
using System.Buffers.Binary;
using System.Text;

namespace RuntimeMetricsLab.Analysis;

public sealed record ProfileFrame(string Module, string Method);
public sealed record CpuObservation(double Milliseconds, int ProcessId, int ThreadId, ulong Weight, ProfileFrame[] Frames);
public sealed record AllocationObservation(double Milliseconds, int ProcessId, int ThreadId, long ObjectSize,
    long SampledByteOffset, string TypeName, ProfileFrame[] Frames);
public sealed record MarkerObservation(double Milliseconds, int ProcessId, string EventName, Dictionary<string, string> Payload);
public sealed record DecodedTrace(CpuObservation[] Cpu, AllocationObservation[] Allocations,
    MarkerObservation[] Markers, MarkerObservation[] IdentityEvents, int EventsLost,
    Dictionary<string, long> EventCounts, Dictionary<string, string> Headers);

/// <summary>Adapter for released TraceEvent 3.2.8 and collect-linux universal nettrace events.</summary>
public static class NetTraceDecoder
{
    private const int MaximumObservations = 1_000_000;
    private const int MaximumStackDepth = 256;

    public static DecodedTrace Decode(string tracePath, string indexPath)
    {
        if (new FileInfo(tracePath).Length > 512L * 1024 * 1024)
            throw new InvalidDataException("Capture exceeds the 512 MiB parser limit.");

        using (var input = File.OpenRead(tracePath))
        {
            Span<byte> prefix = stackalloc byte[20];
            input.ReadExactly(prefix);
            if (!prefix[..8].SequenceEqual(Encoding.ASCII.GetBytes("Nettrace")) ||
                BinaryPrimitives.ReadInt32LittleEndian(prefix[8..12]) != 0 ||
                BinaryPrimitives.ReadInt32LittleEndian(prefix[12..16]) != 6)
                throw new InvalidDataException("This adapter requires the verified universal nettrace version 6.");
        }
        using var raw = new EventPipeEventSource(tracePath);
        var rawEventCount = 0;
        raw.AllEvents += _ =>
        {
            if (++rawEventCount > 2_000_000)
                throw new InvalidDataException("Capture exceeds the two-million-event decoding limit.");
        };
        raw.Process();
        var headers = new Dictionary<string, string>(raw.HeaderKeyValuePairs, StringComparer.Ordinal);
        // Universal dynamic symbols are resolved at conversion completion, before stack consumption.
        TraceLog.CreateFromEventPipeDataFile(tracePath, indexPath);
        using var trace = new TraceLog(indexPath);
        using var source = trace.Events.GetSource();
        var cpu = new List<CpuObservation>();
        var allocations = new List<AllocationObservation>();
        var markers = new List<MarkerObservation>();
        var identities = new List<MarkerObservation>();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var frameCount = 0;

        var universal = new UniversalEventsTraceEventParser(source);
        universal.cpu += data =>
        {
            CheckBound(cpu.Count);
            cpu.Add(new(data.TimeStampRelativeMSec, data.ProcessID, data.ThreadID, data.Value, Frames(data, ref frameCount)));
        };
        source.Clr.AllocationSampling += data =>
        {
            CheckBound(allocations.Count);
            allocations.Add(new(data.TimeStampRelativeMSec, data.ProcessID, data.ThreadID,
                data.ObjectSize, data.SampledByteOffset, data.TypeName, Frames(data, ref frameCount)));
        };
        source.AllEvents += data =>
        {
            var key = $"{data.ProviderName}/{data.EventName}";
            if (counts.Count >= 10_000 && !counts.ContainsKey(key))
                throw new InvalidDataException("Capture exceeds the distinct-event metadata limit.");
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (data.ProviderName == "RuntimeMetricsLab-Attribution")
            {
                if (markers.Count >= 10_000)
                    throw new InvalidDataException("Capture exceeds the lab marker limit.");
                markers.Add(Describe(data));
            }
            else if (data.ProviderName.StartsWith("Universal.", StringComparison.Ordinal) &&
                     data.ProviderName != UniversalEventsTraceEventParser.ProviderName)
            {
                CheckBound(identities.Count);
                identities.Add(Describe(data));
            }
        };
        source.Process();
        return new(cpu.ToArray(), allocations.ToArray(), markers.ToArray(), identities.ToArray(),
            Math.Max(trace.EventsLost, raw.EventsLost), counts, headers);
    }

    internal static MarkerObservation Describe(TraceEvent data) =>
        new(data.TimeStampRelativeMSec, data.ProcessID, $"{data.ProviderName}/{data.EventName}",
            data.PayloadNames.Select((name, index) => (name, value: Convert.ToString(data.PayloadValue(index), CultureInfo.InvariantCulture) ?? ""))
                .ToDictionary(pair => pair.name, pair => pair.value, StringComparer.Ordinal));

    internal static ProfileFrame[] Frames(TraceEvent data, ref int frameCount)
    {
        var frames = new List<ProfileFrame>();
        for (var stack = data.CallStack(); stack is not null; stack = stack.Caller)
        {
            if (++frameCount > 5_000_000)
                throw new InvalidDataException("Capture exceeds the five-million-frame expansion limit.");
            if (frames.Count == MaximumStackDepth)
                throw new InvalidDataException("Stack exceeds the supported depth; capture is not silently truncated.");
            var address = stack.CodeAddress;
            var method = address.FullMethodName;
            frames.Add(new(address.ModuleName,
                string.IsNullOrWhiteSpace(method) ? $"[unresolved 0x{address.Address:x}]" : method));
        }
        if (frames.Count == 0)
            frames.Add(new("", "[unknown stack]"));
        return frames.ToArray();
    }

    private static void CheckBound(int count)
    {
        if (count >= MaximumObservations)
            throw new InvalidDataException("Capture exceeds the supported observation limit.");
    }
}
