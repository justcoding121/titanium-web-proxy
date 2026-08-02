namespace Titanium.Web.Proxy.Models;

/// <summary>
///     Controls which HTTP version the proxy uses on its own connection to the origin server, independent
///     of which HTTP version the client used to talk to the proxy. Set a connection-level default on
///     <see cref="EventArguments.TunnelConnectSessionEventArgs.UpstreamHttpProtocol" /> (during
///     <c>BeforeTunnelConnectRequest</c>), <see cref="EventArguments.BeforeSslAuthenticateEventArgs.UpstreamHttpProtocol" />
///     (during <c>BeforeSslAuthenticate</c>), or
///     <see cref="EventArguments.BeforeQuicAuthenticateEventArgs.UpstreamHttpProtocol" /> (during
///     <c>BeforeQuicAuthenticate</c>). Per-request overrides are available via
///     <see cref="EventArguments.SessionEventArgs.UpstreamHttpProtocol" /> in <c>BeforeRequest</c>.
/// </summary>
public enum UpstreamHttpProtocol
{
    /// <summary>
    ///     Couple the origin protocol to the client protocol: HTTP/2 is only ever offered to the client when
    ///     the origin has also been confirmed (via a fresh probe or a cached prior result) to support HTTP/2,
    ///     and the origin connection then uses whatever protocol the client ends up negotiating. When
    ///     <see cref="ProxyServer.EnableHttp3" /> is <see langword="true" />, a cached Alt-Svc / HTTPS/SVCB
    ///     result in <see cref="Http3.Http3OriginCapabilityCache" /> only arms background QUIC warm-up;
    ///     outbound HTTP/3 is used once that origin is warm, otherwise the request stays on HTTP/2 or
    ///     HTTP/1.1. This is the default.
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
    ///         Honored from connection-level events and from
    ///         <see cref="EventArguments.SessionEventArgs.UpstreamHttpProtocol" /> in <c>BeforeRequest</c>.
    ///         Forced <see cref="Http3" /> skips Auto-mode warm-up gating and fails closed with no TCP fallback.
    ///     </para>
    /// </summary>
    Http3
}
