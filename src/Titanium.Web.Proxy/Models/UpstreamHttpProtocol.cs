namespace Titanium.Web.Proxy.Models;

/// <summary>
///     Controls which HTTP version the proxy uses on its own connection to the origin server, independent
///     of which HTTP version the client used to talk to the proxy. Set on
///     <see cref="EventArguments.TunnelConnectSessionEventArgs.UpstreamHttpProtocol" /> (during
///     <c>BeforeTunnelConnectRequest</c>, for explicit CONNECT tunnels) or
///     <see cref="EventArguments.BeforeSslAuthenticateEventArgs.UpstreamHttpProtocol" /> (during
///     <c>BeforeSslAuthenticate</c>, for transparent endpoints). The choice applies to the whole client TLS
///     connection - it cannot be changed later from <c>BeforeRequest</c>, because the client's own ALPN
///     offer/negotiation has already completed by then. For HTTP/3 (QUIC) connections use
///     <see cref="EventArguments.BeforeQuicAuthenticateEventArgs.UpstreamHttpProtocol" /> instead.
/// </summary>
public enum UpstreamHttpProtocol
{
    /// <summary>
    ///     Couple the origin protocol to the client protocol: HTTP/2 is only ever offered to the client when
    ///     the origin has also been confirmed (via a fresh probe or a cached prior result) to support HTTP/2,
    ///     and the origin connection then uses whatever protocol the client ends up negotiating. When
    ///     <see cref="ProxyServer.EnableHttp3" /> is <see langword="true" />, HTTP/3 is also tried first when
    ///     <see cref="Http2.OriginCapabilityCache" /> has a valid cached Alt-Svc / HTTPS/SVCB result for the
    ///     origin, falling back to HTTP/2 then HTTP/1.1 on failure. This is the default.
    /// </summary>
    Auto,

    /// <summary>
    ///     Always use HTTP/1.1 on the connection to the origin, regardless of what the client negotiates with
    ///     the proxy. When <see cref="EventArguments.TunnelConnectSessionEventArgs.AllowHttpProtocolTranslation" />/
    ///     <see cref="EventArguments.BeforeSslAuthenticateEventArgs.AllowHttpProtocolTranslation" /> is left
    ///     at its default of <c>false</c>, the client is simply never offered "h2" via ALPN either, so it
    ///     transparently negotiates HTTP/1.1 too and no translation is ever required. Setting it to
    ///     <c>true</c> instead allows the client to negotiate HTTP/2 while the origin connection stays
    ///     HTTP/1.1, which requires bridging client h2 streams onto HTTP/1.1 origin requests.
    /// </summary>
    Http11,

    /// <summary>
    ///     Always use HTTP/2 on the connection to the origin, failing the connection outright if the origin
    ///     does not negotiate "h2" via ALPN - a translation bridge cannot fabricate HTTP/2 support at an
    ///     origin that genuinely lacks it. When the client itself does not negotiate HTTP/2, reconciling that
    ///     with a confirmed HTTP/2 origin connection requires
    ///     <see cref="EventArguments.TunnelConnectSessionEventArgs.AllowHttpProtocolTranslation" />/
    ///     <see cref="EventArguments.BeforeSslAuthenticateEventArgs.AllowHttpProtocolTranslation" /> to bridge
    ///     HTTP/1.1 client requests onto the HTTP/2 origin connection.
    /// </summary>
    Http2,

    /// <summary>
    ///     Always use HTTP/3 (QUIC) on the connection to the origin. Fails the stream with
    ///     <see cref="Exceptions.ProxyConnectException" /> if HTTP/3 cannot be established — no fallback to
    ///     HTTP/2 or HTTP/1.1. Symmetric with <see cref="Http2" />: origin must support QUIC/h3 or the
    ///     request fails. When <c>AllowHttpProtocolTranslation</c> is <see langword="true" />, a non-H3
    ///     inbound client connection may still be bridged onto the H3 origin stream.
    ///     <para>
    ///         This value is only meaningful on <see cref="EventArguments.BeforeQuicAuthenticateEventArgs" />.
    ///         Setting it on a transparent TCP endpoint event has no effect because QUIC requires a UDP
    ///         listener configured as <c>TransparentQuicProxyEndPoint</c>.
    ///     </para>
    /// </summary>
    Http3
}
