using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
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

    internal TcpServerConnection(ProxyServer proxyServer, Socket tcpSocket, HttpServerStream stream,
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

    public Guid Id { get; } = Guid.NewGuid();

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
    internal Version Version { get; set; } = HttpHeader.VersionUnknown;

    /// <summary>
    ///     The TcpClient.
    /// </summary>
    internal Socket TcpSocket { get; }

    /// <summary>
    ///     Used to write lines to server
    /// </summary>
    internal HttpServerStream Stream { get; }

    /// <summary>
    ///     Last time this connection was used
    /// </summary>
    internal DateTime LastAccess { get; set; }

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

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        Task.Run(async () =>
        {
            // delay calling tcp connection close()
            // so that server have enough time to call close first.
            // This way we can push tcp Time_Wait to server side when possible.
            await Task.Delay(1000);

            ProxyServer.UpdateServerConnectionCount(false);

            if (disposing)
            {
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
            }
        });

        disposed = true;
    }

    ~TcpServerConnection()
    {
        Logging.ProxyDiagnostics.ReportUndisposedFinalizer(ProxyServer.Logger, nameof(TcpServerConnection));

        Dispose(false);
    }
}