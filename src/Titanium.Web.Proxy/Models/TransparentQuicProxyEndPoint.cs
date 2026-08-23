#pragma warning disable CA1416 // QUIC APIs are platform-specific; runtime check guards usage
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Quic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Network.Quic;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     A transparent proxy endpoint that listens on UDP/QUIC and intercepts HTTP/3 traffic.
///     Clients are not aware of the proxy; traffic must be directed here via firewall/NAT redirection.
///     <para>
///         QUIC always terminates TLS 1.3 at the proxy — there is no pass-through mode. An
///         <see cref="IOriginalDestinationResolver" /> must be configured (or a fixed
///         <see cref="TransparentBaseProxyEndPoint.ForwardHost" /> /
///         <see cref="TransparentBaseProxyEndPoint.ForwardPort" /> fallback) so the proxy knows which
///         origin server each connection is intended for.
///     </para>
///     <para>
///         <b>Platform requirement:</b> <see cref="QuicListener.IsSupported" /> must be
///         <see langword="true" /> (MsQuic native library present, OS version supported). If it is
///         <see langword="false" />, <see cref="ProxyServer.Start" /> will throw
///         <see cref="PlatformNotSupportedException" />.
///     </para>
///     <para>
///         <b>ECH constraint:</b> when managed DNS advertises ECH for intercepted names, the hidden SNI
///         is encrypted and cannot be extracted here. Either disable ECH for intercepted names in your
///         managed DNS, or configure managed clients to disable ECH.
///     </para>
///     <para>
///         <b>Experimental:</b> HTTP/3 support has not yet completed the full interop/soak/fuzz gate
///         process. Suppress <c>TWP001</c> to opt in.
///     </para>
/// </summary>
[DebuggerDisplay("TransparentQuic: {IpAddress}:{Port}")]
[Experimental("TWP001")]
public class TransparentQuicProxyEndPoint : TransparentBaseProxyEndPoint, IQuicInboundEndPoint
{
    private int maxInboundUnidirectionalStreams = 3;

    /// <summary>
    ///     Initializes a new <see cref="TransparentQuicProxyEndPoint" /> listening on all addresses at
    ///     the specified port.
    /// </summary>
    /// <param name="port">UDP port to listen on.</param>
    public TransparentQuicProxyEndPoint(int port)
        : this(IPAddress.Any, port)
    {
    }

    /// <summary>
    ///     Initializes a new <see cref="TransparentQuicProxyEndPoint" />.
    /// </summary>
    /// <param name="ipAddress">Local UDP address to listen on.</param>
    /// <param name="port">UDP port to listen on.</param>
    public TransparentQuicProxyEndPoint(IPAddress ipAddress, int port)
        : base(ipAddress, port, decryptSsl: true)
    {
        GenericCertificateName = "localhost";
    }

    /// <summary>
    ///     Fallback certificate name used when the client does not supply SNI.
    ///     Defaults to <c>"localhost"</c>.
    /// </summary>
    public override string GenericCertificateName { get; set; }

    /// <summary>
    ///     Resolver that determines the original (pre-NAT) destination host and port for each incoming
    ///     QUIC connection. When <see langword="null" /> the endpoint falls back to
    ///     <see cref="TransparentBaseProxyEndPoint.ForwardHost" /> /
    ///     <see cref="TransparentBaseProxyEndPoint.ForwardPort" />; if neither is set the connection is
    ///     rejected.
    /// </summary>
    public IOriginalDestinationResolver? OriginalDestinationResolver { get; set; }

    /// <summary>
    ///     Maximum number of concurrent inbound client-initiated bidirectional streams per QUIC connection
    ///     (HTTP/3 request streams). MsQuic enforces QUIC flow control at this limit — the client is
    ///     backpressured, not disconnected.
    ///     Default: 100.
    /// </summary>
    public int MaxInboundBidirectionalStreams { get; set; } = 100;

    /// <summary>
    ///     Maximum number of concurrent inbound client-initiated unidirectional streams per QUIC connection.
    ///     HTTP/3 requires at least 3 (control stream + QPACK encoder stream + QPACK decoder stream from
    ///     the client). Values below 3 are clamped to 3 at runtime.
    ///     Default: 3.
    /// </summary>
    public int MaxInboundUnidirectionalStreams
    {
        get => maxInboundUnidirectionalStreams;
        set => maxInboundUnidirectionalStreams = Math.Max(3, value);
    }

    /// <summary>
    ///     Maximum time allowed for the QUIC handshake before the connection is aborted.
    ///     Default: 30 seconds.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Connection idle timeout. MsQuic closes idle connections that exceed this duration.
    ///     Default: 60 seconds.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Documented for origin-upgrade scenarios; dual-listen reverse HTTP/3 on
    ///     <see cref="TransparentProxyEndPoint.EnableHttp3" /> is the supported path for client-facing
    ///     <c>Alt-Svc</c> discovery. This flag remains unused on UDP-only endpoints (no H1/H2 listen).
    ///     Default: <see langword="false" />.
    /// </summary>
    public bool AdvertiseToHttpClients { get; set; } = false;

    /// <summary>
    ///     Internal: the underlying <see cref="QuicListener" /> instance. Set and cleared by
    ///     <see cref="ProxyServer" /> on Start/Stop.
    /// </summary>
    internal QuicListener? QuicListener { get; set; }

    /// <summary>
    ///     Transient per-connection <see cref="BeforeQuicAuthenticateEventArgs" /> created in the
    ///     <c>ConnectionOptionsCallback</c> and consumed by the accept loop. Uses a
    ///     <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}" /> so that
    ///     entries are automatically released if the <see cref="QuicConnection" /> is GC'd before the
    ///     accept loop picks it up.
    /// </summary>
    internal ConditionalWeakTable<QuicConnection, BeforeQuicAuthenticateEventArgs>
        PendingQuicAuthArgs { get; } = new();

    QuicListener? IQuicInboundEndPoint.QuicListener
    {
        get => QuicListener;
        set => QuicListener = value;
    }

    ConditionalWeakTable<QuicConnection, BeforeQuicAuthenticateEventArgs> IQuicInboundEndPoint.PendingQuicAuthArgs =>
        PendingQuicAuthArgs;

    IOriginalDestinationResolver? IQuicInboundEndPoint.OriginalDestinationResolver =>
        OriginalDestinationResolver;

    ProxyEndPoint IQuicInboundEndPoint.ProxyEndPoint => this;

    void IQuicInboundEndPoint.AssignPort(int port) => Port = port;

    /// <summary>
    ///     Fired before the QUIC TLS handshake completes for each inbound connection.
    ///     Handlers may inspect/override the forward target, upstream protocol policy, and custom upstream
    ///     proxy, or call <see cref="BeforeQuicAuthenticateEventArgs.Reject" /> to refuse the connection.
    ///     <para>
    ///         This event does <b>not</b> expose <c>DecryptSsl</c>: QUIC always decrypts at the proxy.
    ///     </para>
    /// </summary>
    public event AsyncEventHandler<BeforeQuicAuthenticateEventArgs>? BeforeQuicAuthenticate; // NOSONAR S3264 -- Public extension event invoked by the QUIC pipeline.

    internal Task InvokeBeforeQuicAuthenticate(ProxyServer proxyServer,
        BeforeQuicAuthenticateEventArgs args, ILogger logger)
    {
        return BeforeQuicAuthenticate != null
            ? BeforeQuicAuthenticate.InvokeAsync(proxyServer, args, logger)
            : Task.CompletedTask;
    }

    Task IQuicInboundEndPoint.InvokeBeforeQuicAuthenticate(ProxyServer proxyServer,
        BeforeQuicAuthenticateEventArgs args, ILogger logger) =>
        InvokeBeforeQuicAuthenticate(proxyServer, args, logger);

    /// <summary>
    ///     Not applicable for QUIC endpoints (QUIC always decrypts TLS 1.3). Implemented as a no-op to
    ///     satisfy the <see cref="TransparentBaseProxyEndPoint" /> contract. Use
    ///     <see cref="BeforeQuicAuthenticate" /> instead.
    /// </summary>
    internal override bool HasBeforeSslAuthenticateHandlers => false;

    internal override Task InvokeBeforeSslAuthenticate(ProxyServer proxyServer,
        BeforeSslAuthenticateEventArgs connectArgs, ILogger logger) => Task.CompletedTask;

    internal override Task InvokeBeforeHttpAuthenticate(ProxyServer proxyServer,
        BeforeHttpAuthenticateEventArgs args, ILogger logger) => Task.CompletedTask;
}
