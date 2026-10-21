using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RuntimeMetricsLab.Contracts.Diagnostics;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Web.Api;

/// <summary>
/// Typed client for the experiments API that the RuntimeMetricsLab.ApiService project is expected to expose:
///   GET    /api/experiments
///   POST   /api/experiments/{id}/runs           body: { "preset": string, "target"?: string }
///   GET    /api/experiments/runs/{runId}
///   DELETE /api/experiments/runs/{runId}
///
/// The exact JSON shape of that contract may still change while the backend is being built, so
/// every response is parsed field-by-field from a <see cref="JsonElement"/> using a set of
/// tolerant, case-insensitive candidate property names rather than a strict POCO model. That way
/// small naming differences (e.g. "runId" vs "id", "startedAtUtc" vs "startedAt") don't break the UI.
/// Missing endpoints (404/501/405) are reported distinctly from genuine errors so pages can show a
/// "not implemented yet" state instead of a scary failure.
/// </summary>
public sealed class ExperimentsApiClient(HttpClient httpClient, ILogger<ExperimentsApiClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ApiResult<IReadOnlyList<ExperimentSummary>>> GetExperimentsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(() => httpClient.GetAsync("api/experiments", cancellationToken), cancellationToken);
        if (!response.Success)
        {
            return ApiResult<IReadOnlyList<ExperimentSummary>>.Fail(response.ErrorMessage!, response.StatusCode, response.EndpointUnavailable);
        }

        try
        {
            var root = response.Value!.RootElement;
            var items = root.ValueKind == JsonValueKind.Array
                ? root
                : FindArrayProperty(root, "experiments", "items", "value", "data", "results");

            var list = new List<ExperimentSummary>();
            if (items.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in items.EnumerateArray())
                {
                    var summary = ParseExperimentSummary(element);
                    if (summary is not null)
                    {
                        list.Add(summary);
                    }
                }

            }

            return ApiResult<IReadOnlyList<ExperimentSummary>>.Ok(list);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to parse GET /api/experiments response");
            return ApiResult<IReadOnlyList<ExperimentSummary>>.Fail($"The API returned a response that could not be understood: {ex.Message}");
        }
    }

    public async Task<ApiResult<RuntimeMetricSample>> GetLatestServerCpuMetricsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var sample = await httpClient.GetFromJsonAsync<RuntimeMetricSample>("api/metrics/cpu/latest", JsonOptions, cancellationToken);
            return sample is null
                ? ApiResult<RuntimeMetricSample>.Fail("The server returned an empty runtime metric sample.")
                : ApiResult<RuntimeMetricSample>.Ok(sample);
        }

        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ApiResult<RuntimeMetricSample>.Fail(DescribeTransportException(ex));
        }
    }

    public async Task<ApiResult<RuntimePoint>> GetLatestRuntimeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var sample = await httpClient.GetFromJsonAsync<RuntimePoint>("api/metrics/runtime/latest", JsonOptions, cancellationToken);
            return sample is null ? ApiResult<RuntimePoint>.Fail("Runtime sample unavailable.") : ApiResult<RuntimePoint>.Ok(sample);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(exception, "Runtime sample request failed");
            return ApiResult<RuntimePoint>.Fail(DescribeTransportException(exception));
        }
    }

    public async Task<ApiResult<ExperimentRun>> StartRunAsync(
        string experimentId,
        string preset,
        string? target,
        int? durationSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var body = JsonContent.Create(
            new
            {
                preset,
                target,
                parameters = durationSeconds is null ? null : new { durationSeconds }
            },
            options: JsonOptions);
        var response = await SendAsync(
            () => httpClient.PostAsync($"api/experiments/{Uri.EscapeDataString(experimentId)}/runs", body, cancellationToken),
            cancellationToken);

        if (!response.Success)
        {
            return ApiResult<ExperimentRun>.Fail(response.ErrorMessage!, response.StatusCode, response.EndpointUnavailable);
        }

        return TryParseRun(response.Value!.RootElement, fallbackExperimentId: experimentId, fallbackPreset: preset, fallbackTarget: target);
    }

    public async Task<ApiResult<ExperimentRun>> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            () => httpClient.GetAsync($"api/experiments/runs/{Uri.EscapeDataString(runId)}", cancellationToken),
            cancellationToken);

        if (!response.Success)
        {
            return ApiResult<ExperimentRun>.Fail(response.ErrorMessage!, response.StatusCode, response.EndpointUnavailable);
        }

        return TryParseRun(response.Value!.RootElement, fallbackRunId: runId);
    }

    public async Task<ApiResult<bool>> CancelRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var httpResponse = await httpClient.DeleteAsync($"api/experiments/runs/{Uri.EscapeDataString(runId)}", cancellationToken);
            if (httpResponse.IsSuccessStatusCode)
            {
                return ApiResult<bool>.Ok(true);
            }

            var (message, unavailable) = await DescribeFailureAsync(httpResponse, cancellationToken);
            return ApiResult<bool>.Fail(message, (int)httpResponse.StatusCode, unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiResult<bool>.Fail(DescribeTransportException(ex));
        }
    }

    // --- internal plumbing -------------------------------------------------------------

    private async Task<ApiResult<JsonDocument>> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiResult<JsonDocument>.Fail(DescribeTransportException(ex));
        }

        using (httpResponse)
        {
            if (!httpResponse.IsSuccessStatusCode)
            {
                var (message, unavailable) = await DescribeFailureAsync(httpResponse, cancellationToken);
                return ApiResult<JsonDocument>.Fail(message, (int)httpResponse.StatusCode, unavailable);
            }

            try
            {
                var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
                if (httpResponse.Content.Headers.ContentLength == 0)
                {
                    return ApiResult<JsonDocument>.Ok(JsonDocument.Parse("null"));
                }

                var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                return ApiResult<JsonDocument>.Ok(document);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Response body was not valid JSON");
                return ApiResult<JsonDocument>.Fail("The API returned a response that wasn't valid JSON.");
            }
        }
    }

    private static async Task<(string Message, bool Unavailable)> DescribeFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var unavailable = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed;

        string? detail = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    detail = GetString(doc.RootElement, "detail", "title", "message", "error");
                }
                catch (JsonException)
                {
                    // Not JSON (e.g. plain-text 404 page) - fall back to a trimmed snippet.
                }

                detail ??= text.Length > 300 ? text[..300] + "…" : text;
            }
        }
        catch
        {
            // Reading the body is best-effort; ignore failures here.
        }

        var baseMessage = unavailable
            ? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} — this endpoint may not be implemented by the API service yet."
            : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.";

        return (string.IsNullOrWhiteSpace(detail) ? baseMessage : $"{baseMessage} {detail}", unavailable);
    }

    private static string DescribeTransportException(Exception ex) => ex switch
    {
        TaskCanceledException => "The request to the API service timed out or was cancelled.",
        HttpRequestException http => $"Could not reach the API service ({http.Message}). It may still be starting up.",
        _ => $"Unexpected transport error: {ex.Message}"
    };

    private static ExperimentSummary? ParseExperimentSummary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(element, "id", "experimentId", "key", "slug", "code");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var name = GetString(element, "name", "title", "displayName") ?? id;
        var category = GetString(element, "category", "categoryName", "kind", "group", "type");
        var description = GetString(element, "description", "summary", "details");
        var presets = ParsePresets(element);

        return new ExperimentSummary(id, name, category, description, presets);
    }

    private static IReadOnlyList<ExperimentPresetOption> ParsePresets(JsonElement element)
    {
        var presetsElement = FindArrayProperty(element, "presets", "availablePresets", "presetOptions");
        var results = new List<ExperimentPresetOption>();
        if (presetsElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var preset in presetsElement.EnumerateArray())
        {
            if (preset.ValueKind == JsonValueKind.String)
            {
                var value = preset.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    results.Add(new ExperimentPresetOption(value, value, null));
                }
            }
            else if (preset.ValueKind == JsonValueKind.Object)
            {
                var id = GetString(preset, "id", "preset", "key", "name");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    var name = GetString(preset, "name", "title", "displayName") ?? id;
                    var description = GetString(preset, "description", "summary");
                    var risk = GetString(preset, "riskNote", "risk", "warning");
                    results.Add(new ExperimentPresetOption(id, name, description, risk));
                }
            }
        }

        return results;
    }

    private static ApiResult<ExperimentRun> TryParseRun(
        JsonElement root,
        string? fallbackRunId = null,
        string? fallbackExperimentId = null,
        string? fallbackPreset = null,
        string? fallbackTarget = null)
    {
        try
        {
            // Some APIs nest the run under a wrapper property (e.g. { "run": { ... } }).
            var element = root.ValueKind == JsonValueKind.Object && !HasAny(root, "runId", "id", "status")
                ? FindObjectProperty(root, "run", "data", "value") ?? root
                : root;

            var runId = GetString(element, "runId", "id", "runID") ?? fallbackRunId;
            if (string.IsNullOrWhiteSpace(runId))
            {
                return ApiResult<ExperimentRun>.Fail("The API response did not include a run identifier.");
            }

            var rawStatus = GetString(element, "status", "state", "runStatus");
            var run = new ExperimentRun(
                RunId: runId,
                ExperimentId: GetString(element, "experimentId", "experiment") ?? fallbackExperimentId,
                Preset: GetString(element, "preset", "presetId") ?? fallbackPreset,
                Target: GetString(element, "target") ?? fallbackTarget,
                Status: NormalizeStatus(rawStatus),
                RawStatus: rawStatus,
                StartedAtUtc: GetDate(element, "startedAtUtc", "startedAt", "createdAtUtc", "createdAt"),
                CompletedAtUtc: GetDate(element, "completedAtUtc", "completedAt", "finishedAtUtc", "finishedAt", "endedAt"),
                Message: GetString(element, "message", "statusMessage", "note"),
                ErrorMessage: GetString(element, "error", "errorMessage", "failureReason"),
                Metrics: ParseMetrics(element));

            return ApiResult<ExperimentRun>.Ok(run);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ApiResult<ExperimentRun>.Fail($"The API returned a response that could not be understood: {ex.Message}");
        }
    }

    private static IReadOnlyList<RunMetricSample> ParseMetrics(JsonElement element)
    {
        var results = new List<RunMetricSample>();
        var metrics = FindProperty(element, "metrics", "measurements", "samples");
        if (metrics is not { } metricsElement)
        {
            return results;
        }

        if (metricsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metricsElement.EnumerateObject())
            {
                results.Add(new RunMetricSample(property.Name, DescribeValue(property.Value, out var unit), unit));
            }
        }
        else if (metricsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in metricsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var name = GetString(item, "name", "instrument", "key") ?? "value";
                    var unit = GetString(item, "unit");
                    var valueElement = FindProperty(item, "value", "amount", "measurement");
                    var value = valueElement is { } v ? DescribeValue(v, out var innerUnit) : GetString(item, "value") ?? "?";
                    results.Add(new RunMetricSample(name, value, unit));
                }
                else
                {
                    results.Add(new RunMetricSample($"#{results.Count + 1}", DescribeValue(item, out var unit), unit));
                }
            }
        }

        return results;
    }

    private static string DescribeValue(JsonElement element, out string? unit)
    {
        unit = null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            unit = GetString(element, "unit");
            var nested = GetString(element, "value", "amount");
            return nested ?? element.GetRawText();
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "—",
            _ => element.GetRawText()
        };
    }

    private static RunState NormalizeStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return RunState.Unknown;
        }

        var value = raw.Trim().ToLowerInvariant();
        return value switch
        {
            "pending" or "queued" or "scheduled" or "created" or "notstarted" or "not_started" or "accepted" => RunState.Pending,
            "running" or "inprogress" or "in_progress" or "active" or "started" or "executing" or "cancelling" or "canceling" => RunState.Running,
            "completed" or "complete" or "succeeded" or "success" or "done" or "finished" or "ok" or "coolingdown" or "cooling_down" => RunState.Completed,
            "failed" or "fault" or "faulted" or "error" or "errored" => RunState.Failed,
            "cancelled" or "canceled" or "aborted" or "stopped" => RunState.Cancelled,
            _ => RunState.Unknown
        };
    }

    // --- flexible JSON helpers ---------------------------------------------------------

    private static bool HasAny(JsonElement element, params string[] names) => FindProperty(element, names) is not null;

    private static JsonElement? FindProperty(JsonElement element, params string[] candidateNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            foreach (var candidate in candidateNames)
            {
                if (string.Equals(property.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }
        }

        return null;
    }

    private static JsonElement? FindObjectProperty(JsonElement element, params string[] candidateNames) =>
        FindProperty(element, candidateNames) is { ValueKind: JsonValueKind.Object } found ? found : null;

    private static JsonElement FindArrayProperty(JsonElement element, params string[] candidateNames) =>
        FindProperty(element, candidateNames) is { ValueKind: JsonValueKind.Array } found ? found : default;

    private static string? GetString(JsonElement element, params string[] candidateNames)
    {
        var value = FindProperty(element, candidateNames);
        return value?.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static DateTimeOffset? GetDate(JsonElement element, params string[] candidateNames)
    {
        var value = FindProperty(element, candidateNames);
        if (value is { ValueKind: JsonValueKind.String } str && DateTimeOffset.TryParse(str.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
