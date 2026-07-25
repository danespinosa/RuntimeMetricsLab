namespace RuntimeMetricsLab.Web.Services;

public sealed record LiveMetricPoint(DateTimeOffset Timestamp, double ClientValue, double ServerValue);
