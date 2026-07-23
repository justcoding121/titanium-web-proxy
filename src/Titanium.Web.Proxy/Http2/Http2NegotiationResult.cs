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
    internal Http2NegotiationResult(bool originSupportsHttp2, Task<TcpServerConnection?>? retainedConnectionTask,
        bool requiresHttp11Bridge = false, bool requiresH2OriginBridge = false)
    {
        OriginSupportsHttp2 = originSupportsHttp2;
        RetainedConnectionTask = retainedConnectionTask;
        RequiresHttp11Bridge = requiresHttp11Bridge;
        RequiresH2OriginBridge = requiresH2OriginBridge;
    }

    /// <summary>
    ///     Whether the origin is known - from the capability cache, or from a fresh discovery probe made
    ///     during this call - to support HTTP/2 for the effective route being negotiated.
    /// </summary>
    internal bool OriginSupportsHttp2 { get; }

    /// <summary>
    ///     A not-yet-consumed origin connection opened while negotiating, if any. Its application-protocol
    ///     offer already matches <see cref="OriginSupportsHttp2" /> (or, when <see cref="RequiresH2OriginBridge" />
    ///     is true, is an already-established h2 connection to adopt for the bridge below). Null when no
    ///     connection was opened (HTTP/2 not being considered for this route, prefetch disabled on a cache
    ///     hit, the discovery connection failed, or <see cref="RequiresHttp11Bridge" /> is true).
    /// </summary>
    internal Task<TcpServerConnection?>? RetainedConnectionTask { get; }

    /// <summary>
    ///     True when <see cref="UpstreamHttpProtocol.Http11" /> was requested together with
    ///     <c>AllowHttpProtocolTranslation</c> and the client offered "h2": the origin-facing connection must
    ///     stay HTTP/1.1 (never probed for h2, never touching the shared capability cache), but the client may
    ///     still be offered "h2" because the caller will route the session through the h2-client-to-HTTP/1.1
    ///     translation bridge instead of the normal (protocol-symmetric) relay. <see cref="OriginSupportsHttp2" />
    ///     is always false and <see cref="RetainedConnectionTask" /> is always null when this is true - the
    ///     bridge opens its own per-h2-stream HTTP/1.1 origin connections rather than sharing one retained
    ///     connection across the whole multiplexed client connection.
    /// </summary>
    internal bool RequiresHttp11Bridge { get; }

    /// <summary>
    ///     True when <see cref="UpstreamHttpProtocol.Http2" /> was required together with
    ///     <c>AllowHttpProtocolTranslation</c> and the client does not offer "h2": the client-facing
    ///     connection stays HTTP/1.1 (the client never offered "h2", so nothing changes about what is
    ///     negotiated with it), but every request on it must be translated onto the already-established h2
    ///     origin connection carried in <see cref="RetainedConnectionTask" /> (never null when this is true)
    ///     via the HTTP/1.1-client-to-h2-origin bridge instead of the normal protocol-symmetric HTTP/1.1
    ///     pipeline. <see cref="OriginSupportsHttp2" /> is always true when this is true.
    /// </summary>
    internal bool RequiresH2OriginBridge { get; }
}
