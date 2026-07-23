using System;

namespace Titanium.Web.Proxy.Diagnostics;

/// <summary>
///     Captures the timing of establishing a single upstream (server-facing) TCP/TLS connection. Only
///     populated when <see cref="ProxyServer.EnableRequestTimingCapture" /> is enabled; otherwise no
///     instance is ever allocated.
///     <para>
///         One instance is created per upstream connection, at the moment that connection is first
///         established, and is never mutated afterwards. It is shared by every session that later reuses
///         that same connection from the pool - reachable from any of them via
///         <see cref="EventArguments.SessionEventArgsBase.UpstreamConnectionTiming" />.
///     </para>
///     <para>
///         Address resolution can return more than one IP address for a hostname; the proxy tries them in
///         order until one connects. The duration properties below reflect only the phases of the address
///         that ultimately succeeded - <see cref="FailedAddressAttempts" /> tells you how many earlier
///         addresses were tried and failed first.
///     </para>
/// </summary>
public sealed class UpstreamConnectionTiming
{
    internal UpstreamConnectionTiming(DateTime connectStartedAt)
    {
        ConnectStartedAt = connectStartedAt;
    }

    /// <summary>
    ///     When the proxy started establishing this connection (immediately before DNS resolution).
    /// </summary>
    public DateTime ConnectStartedAt { get; }

    /// <summary>
    ///     When DNS resolution of the connect target (the upstream proxy's hostname, if one is configured
    ///     and in use; otherwise the origin server's hostname) completed.
    /// </summary>
    public DateTime? DnsResolvedAt { get; internal set; }

    /// <summary>
    ///     When the TCP handshake to the (possibly multi-address) connect target completed, on whichever
    ///     address ultimately succeeded.
    /// </summary>
    public DateTime? TcpConnectedAt { get; internal set; }

    /// <summary>
    ///     When the HTTP CONNECT tunnel through an external HTTP(S) upstream proxy was established
    ///     (including any Negotiate/NTLM/Kerberos authentication round trips). <see langword="null" /> when
    ///     no external HTTP upstream proxy is in use for this connection.
    /// </summary>
    public DateTime? UpstreamProxyConnectedAt { get; internal set; }

    /// <summary>
    ///     When the TLS handshake with the origin server completed. <see langword="null" /> for a plain
    ///     (non-HTTPS) connection.
    /// </summary>
    public DateTime? TlsHandshakeCompletedAt { get; internal set; }

    /// <summary>
    ///     When this connection became fully ready to use - the same instant as the last of
    ///     <see cref="TlsHandshakeCompletedAt" />, <see cref="UpstreamProxyConnectedAt" /> or
    ///     <see cref="TcpConnectedAt" /> that applies to this connection.
    /// </summary>
    public DateTime EstablishedAt { get; internal set; }

    /// <summary>
    ///     Number of DNS-resolved addresses that were tried and failed before the address that ultimately
    ///     succeeded. Zero if the first address tried succeeded (the common case).
    /// </summary>
    public int FailedAddressAttempts { get; internal set; }

    /// <summary>How long DNS resolution took.</summary>
    public TimeSpan? DnsDuration => DnsResolvedAt - ConnectStartedAt;

    /// <summary>How long the TCP handshake took, across every address attempted.</summary>
    public TimeSpan? TcpConnectDuration => TcpConnectedAt - (DnsResolvedAt ?? ConnectStartedAt);

    /// <summary>
    ///     How long it took to establish the HTTP CONNECT tunnel through an external upstream proxy.
    ///     <see langword="null" /> when no external HTTP upstream proxy is in use.
    /// </summary>
    public TimeSpan? UpstreamProxyConnectDuration => UpstreamProxyConnectedAt - TcpConnectedAt;

    /// <summary>
    ///     How long the TLS handshake with the origin took. <see langword="null" /> for a plain connection.
    /// </summary>
    public TimeSpan? TlsHandshakeDuration =>
        TlsHandshakeCompletedAt - (UpstreamProxyConnectedAt ?? TcpConnectedAt ?? DnsResolvedAt ?? ConnectStartedAt);

    /// <summary>Total wall-clock time spent establishing this connection, end to end.</summary>
    public TimeSpan TotalDuration => EstablishedAt - ConnectStartedAt;

    internal void MarkDnsResolved()
    {
        DnsResolvedAt = DateTime.UtcNow;
    }

    internal void MarkTcpConnected()
    {
        TcpConnectedAt = DateTime.UtcNow;
    }

    internal void MarkUpstreamProxyConnected()
    {
        UpstreamProxyConnectedAt = DateTime.UtcNow;
    }

    internal void MarkTlsHandshakeCompleted()
    {
        TlsHandshakeCompletedAt = DateTime.UtcNow;
    }

    internal void MarkEstablished()
    {
        EstablishedAt = DateTime.UtcNow;
    }
}
