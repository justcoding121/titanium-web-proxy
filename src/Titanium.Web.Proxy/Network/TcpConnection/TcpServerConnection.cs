using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     An object that holds TcpConnection to a particular server and port
/// </summary>
internal class TcpServerConnection : IDisposable
{
    private bool disposed;

    private int disposalScheduled;

    private int firstUseClaimed;

    internal TcpServerConnection(ProxyServer proxyServer, Socket tcpSocket, HttpServerStream stream, // NOSONAR S107 -- Constructor captures the established connection state without changing internal wiring.
        string hostName, int port, bool isHttps, SslApplicationProtocol negotiatedApplicationProtocol,
        Version version, IExternalProxy? upStreamProxy, IPEndPoint? upStreamEndPoint, string cacheKey)
    {
        TcpSocket = tcpSocket;
        LastAccess = DateTime.UtcNow;
        ProxyServer = proxyServer;
        ProxyServer.UpdateServerConnectionCount(true);
        Stream = stream;
        HostName = hostName;
        Port = port;
        IsHttps = isHttps;
        NegotiatedApplicationProtocol = negotiatedApplicationProtocol;
        Version = version;
        UpStreamProxy = upStreamProxy;
        UpStreamEndPoint = upStreamEndPoint;

        CacheKey = cacheKey;
    }

    public long Id { get; } = ConnectionId.Next();

    /// <summary>
    ///     Structured establishment timing for this connection (DNS/TCP/upstream-proxy/TLS), populated only
    ///     when <see cref="ProxyServer.EnableRequestTimingCapture" /> was enabled at the moment this
    ///     connection was created. Set once by <see cref="Tcp.TcpConnectionFactory" /> right after
    ///     construction and never mutated afterwards; shared by every session that later reuses this
    ///     connection from the pool. Exposed publicly via
    ///     <see cref="EventArguments.SessionEventArgsBase.UpstreamConnectionTiming" />.
    /// </summary>
    internal UpstreamConnectionTiming? Timing { get; set; }

    private ProxyServer ProxyServer { get; }

    internal bool IsClosed => Stream.IsClosed;

    internal IExternalProxy? UpStreamProxy { get; set; }

    internal string HostName { get; set; }

    internal int Port { get; set; }

    internal bool IsHttps { get; set; }

    internal SslApplicationProtocol NegotiatedApplicationProtocol { get; set; }

    /// <summary>
    ///     Local NIC via connection is made
    /// </summary>
    internal IPEndPoint? UpStreamEndPoint { get; set; }

    /// <summary>
    ///     Http version
    /// </summary>
    internal Version Version { get; set; }

    /// <summary>
    ///     The TcpClient.
    /// </summary>
    internal Socket TcpSocket { get; }

    /// <summary>
    ///     Physical peer of the established upstream TCP socket.
    ///     When an upstream proxy is used this is the proxy hop, not the origin.
    /// </summary>
    internal IPEndPoint? RemoteEndPoint => TcpSocket.RemoteEndPoint as IPEndPoint;

    /// <summary>
    ///     Used to write lines to server
    /// </summary>
    internal HttpServerStream Stream { get; }

    /// <summary>
    ///     Last time this connection was used
    /// </summary>
    internal DateTime LastAccess { get; set; }

    /// <summary>
    ///     True once the HTTP/2 connection preface has been written on this connection, i.e. it carries a
    ///     real h2 session rather than being an ALPN-negotiated-but-never-started socket. An h2 connection
    ///     opened only for capability probing/prefetching never starts its session, and origins terminate
    ///     such a connection (typically with GOAWAY/INTERNAL_ERROR) once their preface timeout elapses -
    ///     so it must never be pooled and resurrected later. See <see cref="Tcp.TcpConnectionFactory.Release" />.
    /// </summary>
    internal bool Http2SessionStarted { get; set; }

    /// <summary>
    ///     The cache key used to uniquely identify this connection properties
    /// </summary>
    internal string CacheKey { get; set; }

    /// <summary>
    ///     Is this connection authenticated via WinAuth
    /// </summary>
    internal bool IsWinAuthenticated { get; set; }

    /// <summary>
    ///     True when a per-session client certificate was presented on this TLS connection.
    ///     Such connections are identity-specific and must not be reused from the shared pool.
    /// </summary>
    internal bool UsedClientCertificate { get; set; }

    /// <summary>
    ///     True once this connection has been scheduled for disposal.
    ///     A scheduled connection must never be returned to the pool.
    /// </summary>
    internal bool IsDisposalScheduled => Volatile.Read(ref disposalScheduled) != 0;

    /// <summary>
    ///     Atomically marks this connection as scheduled for disposal.
    ///     Returns true only for the first caller, so the connection is added to the
    ///     disposal bag exactly once (avoids duplicate disposal and an O(n) membership scan).
    /// </summary>
    internal bool TryScheduleDisposal()
    {
        return Interlocked.CompareExchange(ref disposalScheduled, 1, 0) == 0;
    }

    /// <summary>
    ///     Claims this connection for use by a session, for <see cref="ProxyServer.EnableRequestTimingCapture" />'s
    ///     "was the upstream connection reused" bookkeeping. Returns <see langword="true" /> only the very
    ///     first time it is called for this connection's entire lifetime (i.e. the caller is establishing it
    ///     fresh); every subsequent call returns <see langword="false" /> (i.e. the caller is reusing an
    ///     already-claimed connection - whether pooled-and-reacquired, retried, or a multiplexed HTTP/2
    ///     stream sharing a connection another stream already claimed). Cheap and side-effect-free to call
    ///     even when timing capture is disabled.
    /// </summary>
    internal bool ClaimFirstUse()
    {
        return Interlocked.CompareExchange(ref firstUseClaimed, 1, 0) == 0;
    }

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
            // No finalizer: sockets/streams already have their own finalization, and scheduling
            // Task.Run / logging from a finalizer thread is unsafe.
            Task.Run(async () =>
            {
                // delay calling tcp connection close()
                // so that server have enough time to call close first.
                // This way we can push tcp Time_Wait to server side when possible.
                await Task.Delay(1000);

                ProxyServer.UpdateServerConnectionCount(false);

                Stream.Dispose();

                try
                {
                    TcpSocket.Close();
                }
                catch (Exception ex)
                {
                    Logging.ProxyDiagnostics.ReportBenign(ProxyServer.Logger,
                        "Failed to close a server socket during disposal.", ex);
                }
            });
        }

        disposed = true;
    }
}