#pragma warning disable CA1416
using System.Net;
using System.Net.Security;
using System.Security.Authentication;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     Represents the client side of an HTTP/3 QUIC connection for use within the proxy's
///     <see cref="Titanium.Web.Proxy.EventArguments.SessionEventArgsBase" /> hierarchy.
///     Adapts the QUIC connection's endpoint information into the
///     <see cref="TcpClientConnection" /> contract so that the same
///     <see cref="Titanium.Web.Proxy.EventArguments.SessionEventArgs" /> and proxy-event pipeline
///     can be used for both TCP and QUIC sessions.
/// </summary>
internal sealed class QuicClientConnection : TcpClientConnection
{
    internal QuicClientConnection(
        ProxyServer proxyServer,
        IPEndPoint localEndPoint,
        IPEndPoint remoteEndPoint)
        // QUIC clients are counted via Http3ClientConnectionCount, not the TCP client counter.
        : base(proxyServer, localEndPoint, remoteEndPoint, trackClientConnectionCount: false)
    {
        // Seed with TLS 1.3 and H3 since QUIC mandates both.
        SslProtocol = SslProtocols.Tls13;
        NegotiatedApplicationProtocol = SslApplicationProtocol.Http3;
    }
}
#pragma warning restore CA1416
