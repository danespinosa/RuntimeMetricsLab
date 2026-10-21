using System.Buffers.Binary;
using System.Text.Json;

namespace RuntimeMetricsLab.Contracts.Attribution;

public sealed record WindowsCollectorRegistration(int SchemaVersion, string LabId, string OwnerSid,
    int OwnerProcessId, DateTimeOffset OwnerProcessStartUtc, string TargetUrl, string TargetKey,
    string ControlKey, string CollectorExecutable, Guid Generation = default,
    string? ControlUrl = null, WindowsProfilerGrant? Grant = null);

public enum WindowsCollectorOperation { Capabilities, Start, Get, Find, Stop, Shutdown }

public sealed record WindowsCollectorRequest(string Credential, WindowsCollectorOperation Operation,
    Guid Id = default, CollectorStartRequest? Start = null, bool Cancelled = false,
    Guid RegistrationGeneration = default, Guid BrokerGeneration = default);

public sealed record WindowsCollectorReply(CapabilityStatus? Capability = null, CollectorStatus? Capture = null,
    string? Error = null, bool NotFound = false, Guid RegistrationGeneration = default, Guid BrokerGeneration = default);

public static class WindowsCollectorProtocol
{
    public const int RegistrationSchema = 3; // Fresh explicit consent to stock PerfView's system-wide raw ETL.
    private const int MaximumMessageBytes = 64 * 1024;
    public static string PipeName(string labId) => "RuntimeMetricsLab.Attribution.Windows." + AttributionPaths.ValidateLabId(labId);
    public static string PipeName(string labId, Guid registration, Guid broker) =>
        registration != Guid.Empty && broker != Guid.Empty ? $"{PipeName(labId)}.{registration:N}.{broker:N}" :
            throw new ArgumentException("Private broker pipe requires exact registration and broker generations.");

    public static string GrantFile(string labId, Guid attempt) => Path.Combine(AttributionPaths.WindowsDataRoot(labId), $"collector-grant-{attempt:N}.json");
    public static string LaunchFile(string labId, Guid attempt) => Path.Combine(AttributionPaths.WindowsDataRoot(labId), $"collector-launch-{attempt:N}.json");

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Collector control message exceeds its limit.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > MaximumMessageBytes) throw new InvalidDataException("Invalid collector control message length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty collector control message.");
    }
}
