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
///     A proxy end point client is not aware of.
///     Useful when requests are redirected to this proxy end point through port forwarding via router.
///     <para>
///         When <see cref="EnableHttp3" /> is <see langword="true" /> (and
///         <see cref="ProxyServer.EnableHttp3" /> is enabled with <see cref="ProxyEndPoint.DecryptSsl" />),
///         the endpoint also listens for HTTP/3 on the same IP:port over UDP and injects
///         <c>Alt-Svc: h3=":PORT"</c> into H1/H2 responses — reverse HTTPS with Alt-Svc advertisement.
///     </para>
/// </summary>
[DebuggerDisplay("Transparent: {IpAddress}:{Port}")]
public class TransparentProxyEndPoint : TransparentBaseProxyEndPoint, IQuicInboundEndPoint
{
    private int maxInboundUnidirectionalStreams = 3;

    /// <summary>
    ///     Initialize a new instance.
    /// </summary>
    /// <param name="ipAddress">Listening Ip address.</param>
    /// <param name="port">Listening port.</param>
    /// <param name="decryptSsl">Should we decrypt ssl?</param>
    public TransparentProxyEndPoint(IPAddress ipAddress, int port, bool decryptSsl = true) : base(ipAddress, port,
        decryptSsl)
    {
        GenericCertificateName = "localhost";
    }

    /// <summary>
    ///     Name of the Certificate need to be sent (same as the hostname we want to proxy).
    ///     This is valid only when UseServerNameIndication is set to false.
    /// </summary>
    public override string GenericCertificateName { get; set; }

    /// <summary>
    ///     When <see langword="true" />, also accept inbound HTTP/3 (QUIC) on the same IP:port as the
    ///     TCP listener and advertise it via <c>Alt-Svc</c> on H1/H2 responses. Requires
    ///     <see cref="ProxyServer.EnableHttp3" /> and <see cref="ProxyEndPoint.DecryptSsl" />.
    ///     Default: <see langword="false" />.
    ///     <para>
    ///         <b>Experimental:</b> Suppress <c>TWP001</c> to opt in.
    ///     </para>
    /// </summary>
    [Experimental("TWP001")]
    public bool EnableHttp3 { get; set; }

    /// <summary>
    ///     Maximum concurrent inbound client-initiated bidirectional streams per QUIC connection when
    ///     <see cref="EnableHttp3" /> is on. Default: 100.
    /// </summary>
    [Experimental("TWP001")]
    public int MaxInboundBidirectionalStreams { get; set; } = 100;

    /// <summary>
    ///     Maximum concurrent inbound client-initiated unidirectional streams per QUIC connection when
    ///     <see cref="EnableHttp3" /> is on. Values below 3 are clamped to 3. Default: 3.
    /// </summary>
    [Experimental("TWP001")]
    public int MaxInboundUnidirectionalStreams
    {
        get => maxInboundUnidirectionalStreams;
        set => maxInboundUnidirectionalStreams = Math.Max(3, value);
    }

    /// <summary>
    ///     Maximum time allowed for the QUIC handshake when <see cref="EnableHttp3" /> is on.
    ///     Default: 30 seconds.
    /// </summary>
    [Experimental("TWP001")]
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     QUIC connection idle timeout when <see cref="EnableHttp3" /> is on. Default: 60 seconds.
    /// </summary>
    [Experimental("TWP001")]
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Fired before the QUIC TLS handshake completes for each inbound HTTP/3 connection when
    ///     <see cref="EnableHttp3" /> is on.
    /// </summary>
    [Experimental("TWP001")]
    public event AsyncEventHandler<BeforeQuicAuthenticateEventArgs>? BeforeQuicAuthenticate; // NOSONAR S3264 -- Public extension event invoked by the QUIC pipeline.

    /// <summary>
    ///     Before Ssl authentication this event is fired.
    /// </summary>
    public event AsyncEventHandler<BeforeSslAuthenticateEventArgs>? BeforeSslAuthenticate; // NOSONAR S3264 -- Public extension event invoked by the proxy pipeline.

    /// <summary>
    ///     Before handling a cleartext (non-TLS) client session — including prior-knowledge HTTP/2 (h2c) —
    ///     this event is fired so upstream protocol policy can be set without a ClientHello.
    /// </summary>
    public event AsyncEventHandler<BeforeHttpAuthenticateEventArgs>? BeforeHttpAuthenticate; // NOSONAR S3264 -- Public extension event invoked by the proxy pipeline.

    internal QuicListener? QuicListener { get; set; }

    internal ConditionalWeakTable<QuicConnection, BeforeQuicAuthenticateEventArgs> PendingQuicAuthArgs { get; } =
        new();

    QuicListener? IQuicInboundEndPoint.QuicListener
    {
        get => QuicListener;
        set => QuicListener = value;
    }

    ConditionalWeakTable<QuicConnection, BeforeQuicAuthenticateEventArgs> IQuicInboundEndPoint.PendingQuicAuthArgs =>
        PendingQuicAuthArgs;

    IOriginalDestinationResolver? IQuicInboundEndPoint.OriginalDestinationResolver => null;

    ProxyEndPoint IQuicInboundEndPoint.ProxyEndPoint => this;

    void IQuicInboundEndPoint.AssignPort(int port) => Port = port;

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

    internal override bool HasBeforeSslAuthenticateHandlers => BeforeSslAuthenticate != null;

    internal override Task InvokeBeforeSslAuthenticate(ProxyServer proxyServer,
        BeforeSslAuthenticateEventArgs connectArgs, ILogger logger)
    {
        return BeforeSslAuthenticate != null
            ? BeforeSslAuthenticate.InvokeAsync(proxyServer, connectArgs, logger)
            : Task.CompletedTask;
    }

    internal override Task InvokeBeforeHttpAuthenticate(ProxyServer proxyServer,
        BeforeHttpAuthenticateEventArgs args, ILogger logger)
    {
        return BeforeHttpAuthenticate != null
            ? BeforeHttpAuthenticate.InvokeAsync(proxyServer, args, logger)
            : Task.CompletedTask;
    }
}
