using System.Net.Http;
using System.Net.Security;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using RuntimeMetricsLab.Experiments.Abstractions;

namespace RuntimeMetricsLab.ApiService.Http;

/// <summary>
/// Resolves this process's own bound HTTPS address (once Kestrel has started listening) and hands
/// out <see cref="HttpClient"/> instances for the HTTP/TLS lab experiments to call back into this
/// same service over loopback. The dev/self-signed certificate used for local HTTPS is trusted
/// unconditionally here since every call target is this same process.
/// </summary>
public sealed class LoopbackHttpClientProvider : ILoopbackHttpClientProvider, IDisposable
{
    private readonly IServer _server;
    private readonly ILogger<LoopbackHttpClientProvider> _logger;
    private readonly Lazy<Uri> _baseAddress;
    private readonly Lazy<HttpClient> _pooledClient;

    public LoopbackHttpClientProvider(IServer server, ILogger<LoopbackHttpClientProvider> logger)
    {
        _server = server;
        _logger = logger;
        _baseAddress = new Lazy<Uri>(ResolveBaseAddress);
        _pooledClient = new Lazy<HttpClient>(() => CreateClient(pooled: true));
    }

    public Uri BaseAddress => _baseAddress.Value;

    public HttpClient GetPooledClient() => _pooledClient.Value;

    public HttpClient CreateFreshConnectionClient() => CreateClient(pooled: false);

    private HttpClient CreateClient(bool pooled)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
            PooledConnectionLifetime = pooled ? TimeSpan.FromMinutes(2) : TimeSpan.Zero,
        };

        return new HttpClient(handler, disposeHandler: true) { BaseAddress = BaseAddress };
    }

    private Uri ResolveBaseAddress()
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];

        var chosen = addresses.FirstOrDefault(a => a.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No server addresses are bound yet. The loopback HTTP client cannot be resolved before Kestrel starts listening.");

        // Wildcard hosts aren't valid outbound targets; normalize to localhost for loopback calls.
        var normalized = chosen
            .Replace("://+:", "://localhost:", StringComparison.Ordinal)
            .Replace("://*:", "://localhost:", StringComparison.Ordinal)
            .Replace("://[::]:", "://localhost:", StringComparison.Ordinal);

        _logger.LogDebug("Resolved loopback base address {BaseAddress} from bound server addresses.", normalized);
        return new Uri(normalized);
    }

    public void Dispose()
    {
        if (_pooledClient.IsValueCreated)
        {
            _pooledClient.Value.Dispose();
        }
    }
}
