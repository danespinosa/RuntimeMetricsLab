using RuntimeMetricsLab.Contracts.Runs;

namespace RuntimeMetricsLab.ApiService.Endpoints;

/// <summary>
/// Request body accepted by <c>POST /api/experiments/{experimentId}/runs</c>. Accepts both the
/// canonical <c>presetId</c> field and a <c>preset</c> alias for compatibility with clients built
/// against an earlier draft of this contract. <c>target</c> is accepted for forward-compatibility
/// with multi-resource clients but is currently ignored: every experiment in this service always
/// runs in-process against RuntimeMetricsLab.ApiService itself.
/// </summary>
public sealed record StartExperimentRunRequest(string? PresetId, string? Preset, string? Target, ExperimentParameters? Parameters)
{
    public StartExperimentRequest ToDomainRequest() => new(PresetId ?? Preset, Parameters);
}
