namespace RuntimeMetricsLab.Experiments.Abstractions;

/// <summary>
/// Supplies HTTP clients that target this service's own HTTPS endpoint over loopback. Implemented
/// in RuntimeMetricsLab.ApiService (the only project that knows the actual bound addresses); kept
/// as an abstraction here so the Experiments project stays free of ASP.NET Core hosting packages.
/// </summary>
public interface ILoopbackHttpClientProvider
{
    /// <summary>The loopback HTTPS base address workload endpoints are exposed under.</summary>
    Uri BaseAddress { get; }

    /// <summary>A shared, pooled client suitable for repeated calls that should reuse connections.</summary>
    HttpClient GetPooledClient();

    /// <summary>
    /// Creates a client backed by a brand-new handler/connection so the caller can force a fresh
    /// TCP + TLS handshake. The caller owns the returned client and must dispose it.
    /// </summary>
    HttpClient CreateFreshConnectionClient();
}
