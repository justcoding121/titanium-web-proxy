#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network.Quic;

/// <summary>
///     Creates outbound <see cref="QuicServerConnection" /> objects to origin HTTP/3 servers.
///     Analogous to <see cref="Tcp.TcpConnectionFactory" /> but for QUIC.
/// </summary>
internal sealed class QuicConnectionFactory
{
    private readonly ProxyServer _proxyServer;

    internal QuicConnectionFactory(ProxyServer proxyServer)
    {
        _proxyServer = proxyServer;
    }

    /// <summary>
    ///     Establishes a new QUIC connection to <paramref name="connectHost" />:<paramref name="port" />
    ///     and wraps it in a <see cref="QuicServerConnection" />.
    /// </summary>
    /// <param name="connectHost">
    ///     The DNS/hostname used for the UDP QUIC connection. When an HTTPS/SVCB record advertises a
    ///     <c>TargetName</c>, this will differ from <paramref name="sniHost" />.
    /// </param>
    /// <param name="sniHost">
    ///     The hostname presented in the TLS SNI extension and used for certificate validation.
    ///     This is always the origin authority host (the URI host from the HTTP request).
    /// </param>
    /// <exception cref="QuicProxyNotSupportedException">
    ///     Thrown when <paramref name="upStreamProxy" /> is non-null. <c>System.Net.Quic</c> does not
    ///     expose a mechanism for CONNECT tunnelling or SOCKS5 UDP ASSOCIATE; the caller must catch this
    ///     and fall back to a TCP-based bridge so proxy rules are honoured.
    /// </exception>
    internal async Task<QuicServerConnection> CreateAsync(
        string connectHost,
        string sniHost,
        int port,
        IPEndPoint? upStreamEndPoint,
        IExternalProxy? upStreamProxy,
        string cacheKey,
        RemoteCertificateValidationCallback? remoteCertificateValidationCallback,
        CancellationToken cancellationToken)
    {
        if (upStreamProxy != null)
            throw new QuicProxyNotSupportedException(upStreamProxy.ToString() ?? "unknown");

        var clientOptions = new QuicClientConnectionOptions
        {
            // Connect to the SVCB TargetName (or the origin host when no TargetName).
            RemoteEndPoint = new DnsEndPoint(connectHost, port),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 },
                // SNI and certificate validation use the origin authority, not the connect host.
                TargetHost = sniHost,
                RemoteCertificateValidationCallback = remoteCertificateValidationCallback
                    ?? DefaultValidationCallback
            },
            DefaultStreamErrorCode = (long)Http3.Http3ErrorCode.RequestCancelled,
            DefaultCloseErrorCode = (long)Http3.Http3ErrorCode.NoError,
            LocalEndPoint = upStreamEndPoint,
            MaxInboundBidirectionalStreams = 0,
            MaxInboundUnidirectionalStreams = 3  // control, QPACK encoder, QPACK decoder
        };

        var connectStartedAt = DateTime.UtcNow;
        var connection = await QuicConnection.ConnectAsync(clientOptions, cancellationToken);

        // Store the SNI host as the canonical HostName so Alt-Svc / capability cache keying is
        // consistent with the origin identity rather than the transport connect address.
        var serverConnection = new QuicServerConnection(
            _proxyServer, connection, sniHost, port,
            upStreamProxy, upStreamEndPoint, cacheKey);

        if (_proxyServer.EnableRequestTimingCapture)
        {
            var timing = new UpstreamConnectionTiming(connectStartedAt);
            timing.MarkTlsHandshakeCompleted();
            timing.MarkEstablished();
            serverConnection.Timing = timing;
        }

        return serverConnection;
    }

    private bool DefaultValidationCallback(object sender, X509Certificate? certificate,
        X509Chain? chain, SslPolicyErrors sslPolicyErrors)
        => _proxyServer.ValidateServerCertificate(sender, null, certificate, chain, sslPolicyErrors);

    /// <summary>
    ///     Builds the pool cache key for a QUIC origin connection.
    ///     The key encodes the connect target (<paramref name="connectHost" />:<paramref name="port" />),
    ///     the TLS identity (<paramref name="sniHost" />), and the proxy/endpoint coordinates so that
    ///     connections to different SVCB TargetNames for the same origin are kept separate and
    ///     connections to the same target for different origins are never coalesced.
    /// </summary>
    internal static string GetCacheKey(
        string connectHost, int port, string sniHost,
        IExternalProxy? upStreamProxy, IPEndPoint? upStreamEndPoint)
    {
        return $"h3:{connectHost}:{port}:{sniHost}:{upStreamProxy?.ToString() ?? string.Empty}:{upStreamEndPoint?.ToString() ?? string.Empty}";
    }
}
#pragma warning restore CA1416
