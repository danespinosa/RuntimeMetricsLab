namespace RuntimeMetricsLab.Web.Api;

/// <summary>
/// Normalized lifecycle state for an experiment run. The API is expected to return a
/// status string, but the exact vocabulary may change as the backend evolves, so raw
/// values are always preserved alongside this normalized state (see <see cref="ExperimentRun.RawStatus"/>).
/// </summary>
public enum RunState
{
    Unknown,
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>A single named metric sample surfaced for a run, kept as display-ready text.</summary>
public sealed record RunMetricSample(string Name, string Value, string? Unit = null);

/// <summary>A preset offered for an experiment, merged from the API response and/or the local catalog.</summary>
public sealed record ExperimentPresetOption(string Id, string Name, string? Description, string? RiskNote = null);

/// <summary>An experiment as reported by GET /api/experiments.</summary>
public sealed record ExperimentSummary(
    string Id,
    string Name,
    string? Category,
    string? Description,
    IReadOnlyList<ExperimentPresetOption> Presets);

/// <summary>The state of a run as reported by POST .../runs or GET .../runs/{runId}.</summary>
public sealed record ExperimentRun(
    string RunId,
    string? ExperimentId,
    string? Preset,
    string? Target,
    RunState Status,
    string? RawStatus,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Message,
    string? ErrorMessage,
    IReadOnlyList<RunMetricSample> Metrics);

/// <summary>
/// Wraps every API call outcome so the UI can distinguish between success, a
/// reachable-but-erroring backend, and an endpoint that doesn't exist yet (e.g. the
/// contract in RuntimeMetricsLab.ApiService hasn't shipped that route yet).
/// </summary>
public sealed class ApiResult<T>
{
    public bool Success { get; init; }
    public T? Value { get; init; }
    public string? ErrorMessage { get; init; }
    public int? StatusCode { get; init; }
    public bool EndpointUnavailable { get; init; }

    public static ApiResult<T> Ok(T value) => new() { Success = true, Value = value };

    public static ApiResult<T> Fail(string message, int? statusCode = null, bool endpointUnavailable = false) =>
        new() { Success = false, ErrorMessage = message, StatusCode = statusCode, EndpointUnavailable = endpointUnavailable };
}
