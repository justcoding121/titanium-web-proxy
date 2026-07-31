using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http3.Dns;

/// <summary>
///     Probes whether a given host:port advertises HTTP/3 via an HTTPS/SVCB DNS record (RR type 65).
///     Inject a mock implementation in tests.
/// </summary>
internal interface IHttpsSvcbResolver
{
    /// <summary>
    ///     Attempts to discover whether <paramref name="host" />:<paramref name="port" /> supports HTTP/3
    ///     by querying for an HTTPS DNS RR (type 65). Returns a <see cref="SvcbResult" /> on success or
    ///     <see langword="null" /> when no H3 capability is found (NXDOMAIN, SERVFAIL, no <c>alpn=h3</c>
    ///     SvcParam, or timeout).
    /// </summary>
    Task<SvcbResult?> TryGetH3CapabilityAsync(string host, int port, CancellationToken ct);

    /// <summary>
    ///     Removes expired entries from any internal negative-result cache and enforces any backstop
    ///     size cap. Called periodically from <see cref="ProxyServer.TrimOriginCapabilityCaches" />
    ///     (driven by the connection-pool cleanup loop) so a resolver that caches definitive negative
    ///     results does not grow unbounded purely from TTL expiry with no eviction. A no-op for
    ///     resolvers that hold no such state.
    /// </summary>
    void TrimExpired();
}
