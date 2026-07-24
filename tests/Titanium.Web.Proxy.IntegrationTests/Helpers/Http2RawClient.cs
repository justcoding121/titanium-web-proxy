using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     A minimal, hand-rolled HTTP/2 client used to exercise proxy behavior a real HTTP/2 client
///     (<see cref="System.Net.Http.SocketsHttpHandler" />) either has no public API for (sending request
///     trailers, splitting a request header block across CONTINUATION frames) or does not reliably surface
///     to test code (interim 1xx informational responses over h2). Establishes a real HTTP CONNECT tunnel
///     through the proxy under test, then performs a real TLS/ALPN "h2" handshake with the (proxy-generated,
///     MITM'd) leaf certificate for the target host - trusting it the same way <see cref="TestProxyServer" />
///     configures the proxy to trust upstream certificates, via <see cref="TestCertificateAuthority" /> - so
///     everything downstream of the socket is indistinguishable, from the proxy's point of view, from a real
///     HTTP/2 browser/client. See <see cref="Http2RawFrame" /> for the underlying frame helpers, shared with
///     <see cref="Http2RawOriginServer" />.
/// </summary>
internal sealed class Http2RawClient : IDisposable
{
    private readonly TcpClient tcpClient;

    private Http2RawClient(TcpClient tcpClient, Http2RawFrame.Connection connection)
    {
        this.tcpClient = tcpClient;
        Connection = connection;
    }

    public Http2RawFrame.Connection Connection { get; }

    public static Task<Http2RawClient> ConnectAsync(int proxyPort, string targetHost, int targetPort)
    {
        return ConnectAsync(proxyPort, targetHost, targetPort, null);
    }

    /// <summary>
    ///     Same as <see cref="ConnectAsync(int, string, int)" /> but declares
    ///     <paramref name="headerTableSize" /> in the initial SETTINGS frame instead of omitting it (real
    ///     browsers like Chrome advertise a value here) - see
    ///     <see cref="Http2RawFrame.Connection.SendInitialSettingsAsync(int)" />.
    /// </summary>
    public static Task<Http2RawClient> ConnectAsync(int proxyPort, string targetHost, int targetPort,
        int headerTableSize)
    {
        return ConnectAsync(proxyPort, targetHost, targetPort, (int?)headerTableSize);
    }

    private static async Task<Http2RawClient> ConnectAsync(int proxyPort, string targetHost, int targetPort,
        int? headerTableSize)
    {
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("localhost", proxyPort);

        var networkStream = tcpClient.GetStream();
        var connectRequest = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\nHost: {targetHost}:{targetPort}\r\n\r\n";
        var connectBytes = Encoding.ASCII.GetBytes(connectRequest);
        await networkStream.WriteAsync(connectBytes, 0, connectBytes.Length);

        await ReadUntilBlankLineAsync(networkStream);

        return await FromTcpAndTlsAsync(tcpClient, networkStream, targetHost, headerTableSize);
    }

    /// <summary>
    ///     Connects directly to a transparent/reverse-proxy endpoint (no CONNECT tunnel) and performs a
    ///     real TLS/ALPN "h2" handshake using <paramref name="sniHost" /> as the SNI/target host - the same
    ///     way a real HTTP/2 browser talking to a transparent proxy would, with no prior knowledge that a
    ///     proxy is even present.
    /// </summary>
    public static async Task<Http2RawClient> ConnectDirectAsync(int proxyPort, string sniHost)
    {
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("localhost", proxyPort);

        return await FromTcpAndTlsAsync(tcpClient, tcpClient.GetStream(), sniHost, null);
    }

    private static async Task<Http2RawClient> FromTcpAndTlsAsync(TcpClient tcpClient, Stream networkStream,
        string targetHost, int? headerTableSize)
    {
        var sslStream = new SslStream(networkStream, false,
            (_, certificate, chain, errors) => TestCertificateAuthority.Validate(certificate, errors));
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            ApplicationProtocols = new System.Collections.Generic.List<SslApplicationProtocol>
                { SslApplicationProtocol.Http2 },
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
        });

        await sslStream.WriteAsync(Http2Helper.ConnectionPreface, 0, Http2Helper.ConnectionPreface.Length);

        var connection = new Http2RawFrame.Connection(sslStream);
        if (headerTableSize.HasValue)
        {
            await connection.SendInitialSettingsAsync(headerTableSize.Value);
        }
        else
        {
            await connection.SendInitialSettingsAsync();
        }

        return new Http2RawClient(tcpClient, connection);
    }

    /// <summary>
    ///     Establishes a CONNECT tunnel and performs a TLS handshake offering exactly
    ///     <paramref name="alpnOffer" /> via ALPN - unlike <see cref="ConnectAsync(int, string, int)" />,
    ///     which always offers exactly "h2" - so tests can exercise policy decisions that depend on what the
    ///     client itself is willing to speak (e.g. an h1.1-only client, or a client offering both "h2" and
    ///     "http/1.1" and observing which one the proxy actually picks). Does not assume the negotiated
    ///     protocol; callers inspect <see cref="TunnelTlsConnection.NegotiatedApplicationProtocol" /> and
    ///     drive the raw <see cref="TunnelTlsConnection.SslStream" /> themselves (as plain HTTP/1.1 text, or
    ///     by wrapping it in an <see cref="Http2RawFrame.Connection" /> for HTTP/2).
    /// </summary>
    public static async Task<TunnelTlsConnection> ConnectTunnelWithAlpnAsync(int proxyPort, string targetHost,
        int targetPort, System.Collections.Generic.List<SslApplicationProtocol>? alpnOffer)
    {
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("localhost", proxyPort);

        var networkStream = tcpClient.GetStream();
        var connectRequest = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\nHost: {targetHost}:{targetPort}\r\n\r\n";
        var connectBytes = Encoding.ASCII.GetBytes(connectRequest);
        await networkStream.WriteAsync(connectBytes, 0, connectBytes.Length);

        await ReadUntilBlankLineAsync(networkStream);

        var sslStream = new SslStream(networkStream, false,
            (_, certificate, chain, errors) => TestCertificateAuthority.Validate(certificate, errors));
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            ApplicationProtocols = alpnOffer,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
        });

        return new TunnelTlsConnection(tcpClient, sslStream);
    }

    /// <summary>
    ///     A raw CONNECT-tunneled TLS connection whose negotiated ALPN protocol was not assumed up front; see
    ///     <see cref="ConnectTunnelWithAlpnAsync" />.
    /// </summary>
    public sealed class TunnelTlsConnection : IDisposable
    {
        private readonly TcpClient tcpClient;

        internal TunnelTlsConnection(TcpClient tcpClient, SslStream sslStream)
        {
            this.tcpClient = tcpClient;
            SslStream = sslStream;
        }

        public SslStream SslStream { get; }

        public SslApplicationProtocol NegotiatedApplicationProtocol => SslStream.NegotiatedApplicationProtocol;

        /// <summary>
        ///     Wraps this connection's already-authenticated <see cref="SslStream" /> as an HTTP/2 connection:
        ///     sends the client connection preface and initial SETTINGS frame. Only valid to call when
        ///     <see cref="NegotiatedApplicationProtocol" /> is <see cref="SslApplicationProtocol.Http2" />.
        /// </summary>
        public async Task<Http2RawFrame.Connection> StartHttp2Async()
        {
            await SslStream.WriteAsync(Http2Helper.ConnectionPreface, 0, Http2Helper.ConnectionPreface.Length);

            var connection = new Http2RawFrame.Connection(SslStream);
            await connection.SendInitialSettingsAsync();
            return connection;
        }

        public void Dispose()
        {
            tcpClient.Dispose();
        }
    }

    /// <summary>
    ///     Reads (and discards) bytes until the terminating blank line ("\r\n\r\n") of the proxy's CONNECT
    ///     response has been consumed, leaving the stream positioned exactly at the first byte of the TLS
    ///     handshake that follows.
    /// </summary>
    private static async Task ReadUntilBlankLineAsync(Stream stream)
    {
        const string terminator = "\r\n\r\n";
        var buffer = new byte[1];
        var matched = 0;
        while (matched < terminator.Length)
        {
            var read = await stream.ReadAsync(buffer, 0, 1);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "Proxy closed the connection before completing the CONNECT handshake.");
            }

            matched = buffer[0] == terminator[matched] ? matched + 1 : buffer[0] == terminator[0] ? 1 : 0;
        }
    }

    public void Dispose()
    {
        tcpClient.Dispose();
    }
}
