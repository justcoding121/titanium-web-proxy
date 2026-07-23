using System.Threading.Tasks;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Records the outcome of deciding whether an origin supports HTTP/2, together with ownership of any
///     origin connection that had to be opened (as a discovery probe on a cold capability cache, or as a
///     correctly-keyed prefetch on a cache hit) to make that decision. The caller must adopt, pool, or
///     close <see cref="RetainedConnectionTask" /> exactly once; it has not been consumed and no HTTP/2
///     bytes have been written to or read from it beyond the TLS/ALPN handshake itself.
/// </summary>
internal sealed class Http2NegotiationResult
{
    internal Http2NegotiationResult(bool originSupportsHttp2, Task<TcpServerConnection?>? retainedConnectionTask)
    {
        OriginSupportsHttp2 = originSupportsHttp2;
        RetainedConnectionTask = retainedConnectionTask;
    }

    /// <summary>
    ///     Whether the origin is known - from the capability cache, or from a fresh discovery probe made
    ///     during this call - to support HTTP/2 for the effective route being negotiated.
    /// </summary>
    internal bool OriginSupportsHttp2 { get; }

    /// <summary>
    ///     A not-yet-consumed origin connection opened while negotiating, if any. Its application-protocol
    ///     offer already matches <see cref="OriginSupportsHttp2" />. Null when no connection was opened
    ///     (HTTP/2 not being considered for this route, prefetch disabled on a cache hit, or the discovery
    ///     connection failed).
    /// </summary>
    internal Task<TcpServerConnection?>? RetainedConnectionTask { get; }
}
