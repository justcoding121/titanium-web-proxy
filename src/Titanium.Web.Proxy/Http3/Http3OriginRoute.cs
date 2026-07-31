using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Carries the resolved H3 routing decision for one upstream origin, produced by
///     <see cref="ProxyServer.ResolveHttp3OriginAsync" /> and consumed by
///     <see cref="Http3OriginBridge" /> and the H2→H3 bridge handler.
/// </summary>
internal readonly struct Http3OriginRoute
{
    /// <summary>Sentinel value meaning "do not use HTTP/3 for this origin".</summary>
    public static readonly Http3OriginRoute None = default;

    /// <summary>Whether HTTP/3 (QUIC) should be used for this origin.</summary>
    public bool UseH3 { get; init; }

    /// <summary>
    ///     Effective QUIC port to connect to.  Equal to the URI port unless an Alt-Svc or SVCB
    ///     record specifies an alternative port (e.g. <c>h3=":8443"</c> on a connection originally
    ///     to port 443).
    /// </summary>
    public int QuicPort { get; init; }

    /// <summary>
    ///     Effective QUIC connect hostname.  Non-null when an HTTPS/SVCB record specifies a
    ///     <c>TargetName</c> that differs from the origin authority; the QUIC connection is then
    ///     established to this host while the original origin authority is still used for TLS SNI
    ///     and the <c>:authority</c> pseudo-header.
    ///     <see langword="null" /> means use the same host as the origin authority.
    /// </summary>
    public string? QuicHost { get; init; }

    /// <summary>
    ///     When <see langword="true"/>, this route was produced by an explicit
    ///     <see cref="UpstreamHttpProtocol.Http3"/> policy and no TCP fallback is permitted.
    ///     When <see langword="false"/> (<see cref="UpstreamHttpProtocol.Auto"/>), the caller may
    ///     fall back to TCP if QUIC is unavailable or fails.
    /// </summary>
    public bool ForcedH3 { get; init; }

    /// <summary>How this route was discovered, for diagnostics and logging.</summary>
    public Http3RouteSource Source { get; init; }
}

/// <summary>Identifies how an <see cref="Http3OriginRoute"/> was discovered.</summary>
internal enum Http3RouteSource
{
    /// <summary>Not an H3 route — <see cref="Http3OriginRoute.UseH3"/> is <see langword="false"/>.</summary>
    None,
    /// <summary>Explicit <see cref="UpstreamHttpProtocol.Http3"/> policy override.</summary>
    Forced,
    /// <summary>In-memory Alt-Svc capability cache, populated from a prior response header.</summary>
    AltSvcCache,
    /// <summary>HTTPS/SVCB DNS record lookup.</summary>
    HttpsSvcb,
}
