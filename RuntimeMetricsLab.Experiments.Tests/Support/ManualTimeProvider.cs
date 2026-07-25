namespace RuntimeMetricsLab.Experiments.Tests.Support;

/// <summary>
/// A <see cref="TimeProvider"/> whose current time only advances when explicitly told to, so
/// cooldown/timing behavior in coordinator tests is deterministic and never flaky.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;

    public void SetUtcNow(DateTimeOffset value) => _now = value;
}
