using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http3.Dns;

/// <summary>
///     Stand-in <see cref="IHttpsSvcbResolver" /> used when no usable DNS server is configured for
///     HTTPS/SVCB discovery. Always reports "no H3 capability found" instead of the property getter
///     throwing, so any direct caller of <see cref="ProxyServer.HttpsSvcbResolver" /> (present or
///     future, inside or outside this assembly) degrades gracefully rather than crashing. The normal
///     background-discovery path already avoids calling the resolver at all in this situation via
///     <see cref="Http3SvcbDiscoveryCoordinator.IsUsableDnsServer" />; this is a defensive backstop.
/// </summary>
internal sealed class NoOpHttpsSvcbResolver : IHttpsSvcbResolver
{
    internal static readonly NoOpHttpsSvcbResolver Instance = new();

    private NoOpHttpsSvcbResolver()
    {
    }

    public Task<SvcbResult?> TryGetH3CapabilityAsync(string host, int port, CancellationToken ct)
        => Task.FromResult<SvcbResult?>(null);

    public void TrimExpired()
    {
        // No internal state to trim.
    }
}
