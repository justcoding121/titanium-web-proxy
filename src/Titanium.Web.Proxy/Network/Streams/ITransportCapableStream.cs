namespace Titanium.Web.Proxy.StreamExtended.Network;

/// <summary>
///     Internal capability marker for transports whose underlying stream is a real duplex network
///     transport - either a plain socket (<see cref="System.Net.Sockets.NetworkStream" />) or a
///     TLS-wrapped connection (<see cref="System.Net.Security.SslStream" />). Used only to decide whether
///     the per-chunk body-write hook (<c>OnRequestBodyWrite</c> / <c>OnResponseBodyWrite</c>) is safe/useful
///     to invoke for a given reader/writer pair.
///     <para>
///         This is intentionally kept internal rather than added as a member of the public
///         <see cref="IHttpStreamWriter" />/<see cref="IHttpStreamReader" /> interfaces, so that external
///         implementers of those public interfaces are not source-broken by this capability check. An
///         external implementation that does not also implement this internal interface is simply treated
///         as not supporting the hook (preserving today's no-hook behavior for it).
///     </para>
/// </summary>
internal interface ITransportCapableStream
{
    /// <summary>
    ///     True when the underlying transport is a plain <see cref="System.Net.Sockets.NetworkStream" /> or
    ///     a TLS-wrapped <see cref="System.Net.Security.SslStream" />; false for in-memory, decompression,
    ///     or other non-network-backed streams.
    /// </summary>
    bool SupportsBodyWriteHook { get; }
}
