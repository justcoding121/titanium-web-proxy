using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     An object that holds TcpConnection to a particular server and port
/// </summary>
internal class TcpClientConnection : IDisposable
{
    private readonly Socket? tcpClientSocket;

    private bool disposed;

    private int? processId;

    /// <summary>
    ///     When false, this adapter does not participate in <see cref="ProxyServer.ClientConnectionCount" />
    ///     (used by QUIC clients, which are tracked via <see cref="ProxyServer.Http3ClientConnectionCount" />).
    /// </summary>
    private readonly bool trackClientConnectionCount;

    internal TcpClientConnection(ProxyServer proxyServer, Socket tcpClientSocket)
    {
        this.tcpClientSocket = tcpClientSocket;
        ProxyServer = proxyServer;
        trackClientConnectionCount = true;
        ProxyServer.UpdateClientConnectionCount(true);
    }

    /// <summary>
    ///     Protected constructor for subclasses (e.g., QUIC connections) that do not use a TCP socket.
    ///     The caller is responsible for supplying the effective <see cref="LocalEndPoint" /> and
    ///     <see cref="RemoteEndPoint" /> via the <paramref name="localEndPoint" /> and
    ///     <paramref name="remoteEndPoint" /> parameters.
    /// </summary>
    /// <param name="trackClientConnectionCount">
    ///     Pass <see langword="false" /> for QUIC adapters so they do not inflate the TCP-only
    ///     <see cref="ProxyServer.ClientConnectionCount" />.
    /// </param>
    protected TcpClientConnection(ProxyServer proxyServer, IPEndPoint localEndPoint, IPEndPoint remoteEndPoint,
        bool trackClientConnectionCount = true)
    {
        tcpClientSocket = null;
        LocalEndPointOverride = localEndPoint;
        RemoteEndPointOverride = remoteEndPoint;
        ProxyServer = proxyServer;
        this.trackClientConnectionCount = trackClientConnectionCount;
        if (trackClientConnectionCount)
            ProxyServer.UpdateClientConnectionCount(true);
    }

    /// <summary>Stored local endpoint for socket-less subclasses (e.g., QUIC).</summary>
    private IPEndPoint? LocalEndPointOverride { get; }

    /// <summary>Stored remote endpoint for socket-less subclasses (e.g., QUIC).</summary>
    private IPEndPoint? RemoteEndPointOverride { get; }

    public object? ClientUserData { get; set; }

    private ProxyServer ProxyServer { get; }

    public long Id { get; } = ConnectionId.Next();

    /// <summary>
    ///     Accept-thread zero-byte <see cref="Socket.ReceiveAsync(System.Memory{byte}, SocketFlags)" /> —
    ///     must complete before any socket read (<see cref="SslStream.AuthenticateAsServerAsync" /> /
    ///     ClientHello peek). Cleared after await.
    /// </summary>
    internal Task<int>? PendingClientHelloWait { get; set; }

    public EndPoint LocalEndPoint => LocalEndPointOverride
                                     ?? tcpClientSocket?.LocalEndPoint
                                     ?? throw new InvalidOperationException("Client connection has no local endpoint.");

    public EndPoint RemoteEndPoint => RemoteEndPointOverride
                                      ?? tcpClientSocket?.RemoteEndPoint
                                      ?? throw new InvalidOperationException("Client connection has no remote endpoint.");

    internal SslProtocols SslProtocol { get; set; }

    internal SslApplicationProtocol NegotiatedApplicationProtocol { get; set; }

    /// <summary>
    ///     True when this client spoke prior-knowledge cleartext HTTP/2 (h2c) rather than TLS ALPN <c>h2</c>.
    /// </summary>
    internal bool Http2CleartextClient { get; set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        if (disposing)
        {
            // Prefer peer-first close (push TIME_WAIT to the client) only when explicitly requested.
            // Default TcpTimeWaitSeconds==0: close immediately — matches Kestrel SocketConnection
            // and avoids scheduling ~1 Task.Delay per new-connection handshake onto the worker pool.
            if (ProxyServer.TcpTimeWaitSeconds == 0)
            {
                if (trackClientConnectionCount)
                    ProxyServer.UpdateClientConnectionCount(false);

                if (tcpClientSocket != null)
                {
                    try
                    {
                        // Abortive RST (Linger true,0) — applied at close so accept→TLS does not
                        // pay setsockopt; omitting linger falls back to graceful FIN / TIME_WAIT.
                        tcpClientSocket.LingerState = new LingerOption(true, 0);
                        tcpClientSocket.Close();
                    }
                    catch (Exception ex)
                    {
                        Logging.ProxyDiagnostics.ReportBenign(ProxyServer.Logger,
                            "Failed to close a client socket during disposal.", ex);
                    }
                }
            }
            else
            {
                Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    if (trackClientConnectionCount)
                        ProxyServer.UpdateClientConnectionCount(false);

                    if (tcpClientSocket == null) return;
                    try
                    {
                        tcpClientSocket.LingerState =
                            new LingerOption(true, ProxyServer.TcpTimeWaitSeconds);
                        tcpClientSocket.Close();
                    }
                    catch (Exception ex)
                    {
                        Logging.ProxyDiagnostics.ReportBenign(ProxyServer.Logger,
                            "Failed to close a client socket during disposal.", ex);
                    }
                });
            }
        }

        disposed = true;
    }

    public Stream GetStream()
    {
        if (tcpClientSocket == null)
            throw new InvalidOperationException(
                "GetStream() is not supported on non-TCP connections. Use the QUIC stream API directly.");
        return new NetworkStream(tcpClientSocket, true);
    }

    public int GetProcessId(ProxyEndPoint endPoint)
    {
        if (tcpClientSocket == null) return -1; // Process ID is not available for QUIC/non-TCP connections.

        if (processId.HasValue) return processId.Value;

        if (RunTime.IsWindows)
        {
            var remoteEndPoint = (IPEndPoint)RemoteEndPoint;

            // If client is localhost get the process id
            if (NetworkHelper.IsLocalIpAddress(remoteEndPoint.Address))
                processId = TcpHelper.GetProcessIdByLocalPort(endPoint.IpAddress.AddressFamily, remoteEndPoint.Port);
            else
                // can't access process Id of remote request from remote machine
                processId = -1;

            return processId.Value;
        }

        throw new PlatformNotSupportedException();
    }
}