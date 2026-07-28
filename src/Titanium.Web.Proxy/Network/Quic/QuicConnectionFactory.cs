#if NET6_0_OR_GREATER
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
    ///     Establishes a new QUIC connection to <paramref name="hostName" />:<paramref name="port" />
    ///     and wraps it in a <see cref="QuicServerConnection" />.
    /// </summary>
    internal async Task<QuicServerConnection> CreateAsync(
        string hostName,
        int port,
        IPEndPoint? upStreamEndPoint,
        IExternalProxy? upStreamProxy,
        string cacheKey,
        RemoteCertificateValidationCallback? remoteCertificateValidationCallback,
        CancellationToken cancellationToken)
    {
        var clientOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new DnsEndPoint(hostName, port),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 },
                TargetHost = hostName,
                RemoteCertificateValidationCallback = remoteCertificateValidationCallback
                    ?? DefaultValidationCallback
            },
            DefaultStreamErrorCode = (long)Http3.Http3ErrorCode.RequestCancelled,
            DefaultCloseErrorCode = (long)Http3.Http3ErrorCode.NoError,
            LocalEndPoint = upStreamEndPoint,
            MaxInboundBidirectionalStreams = 0, // client does not accept server-initiated bidirectional streams
            MaxInboundUnidirectionalStreams = 3  // control, QPACK encoder, QPACK decoder
        };

        var connectStartedAt = DateTime.UtcNow;
        var connection = await QuicConnection.ConnectAsync(clientOptions, cancellationToken);

        var serverConnection = new QuicServerConnection(
            _proxyServer, connection, hostName, port,
            upStreamProxy, upStreamEndPoint, cacheKey);

        if (_proxyServer.EnableRequestTimingCapture)
        {
            var timing = new UpstreamConnectionTiming(connectStartedAt);
            timing.MarkTlsHandshakeCompleted(); // QUIC combines transport + TLS
            timing.MarkEstablished();
            serverConnection.Timing = timing;
        }

        return serverConnection;
    }

    private bool DefaultValidationCallback(object sender, X509Certificate? certificate,
        X509Chain? chain, SslPolicyErrors sslPolicyErrors)
        => _proxyServer.ValidateServerCertificate(sender, null, certificate, chain, sslPolicyErrors);

    /// <summary>
    ///     Builds the pool cache key for a QUIC origin connection, consistent with the TCP pool format.
    /// </summary>
    internal static string GetCacheKey(string hostName, int port, IExternalProxy? upStreamProxy,
        IPEndPoint? upStreamEndPoint)
    {
        return $"h3:{hostName}:{port}:{upStreamProxy?.ToString() ?? string.Empty}:{upStreamEndPoint?.ToString() ?? string.Empty}";
    }
}
#pragma warning restore CA1416
#endif
