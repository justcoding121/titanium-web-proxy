#pragma warning disable CA1416
using System;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network.Quic;

/// <summary>
///     Holds an open QUIC connection to an origin server. Unlike HTTP/1.1 and HTTP/2 where one
///     <see cref="Tcp.TcpServerConnection" /> carries exactly one concurrent request (HTTP/1.1) or
///     multiplexed streams (HTTP/2), a <see cref="QuicServerConnection" /> carries arbitrarily many
///     concurrent HTTP/3 request streams opened via <see cref="OpenRequestStreamAsync" />.
/// </summary>
internal sealed class QuicServerConnection : IAsyncDisposable
{
    private bool _disposed;
    private int _disposalScheduled;
    private int _firstUseClaimed;

    internal QuicServerConnection(
        ProxyServer proxyServer,
        QuicConnection connection,
        string hostName,
        int port,
        IExternalProxy? upStreamProxy,
        IPEndPoint? upStreamEndPoint,
        string cacheKey)
    {
        Connection = connection;
        LastAccess = DateTime.UtcNow;
        ProxyServer = proxyServer;
        ProxyServer.UpdateServerConnectionCount(true);
        HostName = hostName;
        Port = port;
        UpStreamProxy = upStreamProxy;
        UpStreamEndPoint = upStreamEndPoint;
        CacheKey = cacheKey;
        NegotiatedApplicationProtocol = SslApplicationProtocol.Http3;
    }

    public Guid Id { get; } = Guid.NewGuid();

    private ProxyServer ProxyServer { get; }

    internal QuicConnection Connection { get; }

    internal string HostName { get; set; }

    internal int Port { get; set; }

    internal bool IsHttps => true; // QUIC always uses TLS

    internal SslApplicationProtocol NegotiatedApplicationProtocol { get; }

    internal IExternalProxy? UpStreamProxy { get; set; }

    internal IPEndPoint? UpStreamEndPoint { get; set; }

    internal DateTime LastAccess { get; set; }

    internal string CacheKey { get; }

    /// <summary>
    ///     Structured establishment timing (DNS/UDP/QUIC-handshake), populated when
    ///     <see cref="ProxyServer.EnableRequestTimingCapture" /> is enabled.
    /// </summary>
    internal UpstreamConnectionTiming? Timing { get; set; }

    /// <summary>
    ///     <see langword="true" /> if the connection has been closed by the remote or is otherwise
    ///     unusable. Quick check using the QUIC connection state (not a round-trip).
    /// </summary>
    internal bool IsClosed => _disposed || _disposalScheduled != 0;

    /// <summary>
    ///     Opens a new bidirectional HTTP/3 request stream on this QUIC connection.
    /// </summary>
    internal System.Threading.Tasks.ValueTask<QuicStream> OpenRequestStreamAsync(
        System.Threading.CancellationToken cancellationToken)
    {
        LastAccess = DateTime.UtcNow;
        return Connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);
    }

    /// <summary>
    ///     Claims this connection for use by a session (for timing capture; safe to call once per reuse).
    /// </summary>
    internal bool ClaimFirstUse() => System.Threading.Interlocked.Exchange(ref _firstUseClaimed, 1) == 0;

    /// <summary>
    ///     Schedules this connection for disposal after the current request finishes.
    ///     Returns <see langword="true" /> if the caller was the one to schedule it (i.e. it was
    ///     previously unscheduled), <see langword="false" /> if already scheduled.
    /// </summary>
    internal bool TryScheduleDisposal()
        => System.Threading.Interlocked.CompareExchange(ref _disposalScheduled, 1, 0) == 0;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        ProxyServer.UpdateServerConnectionCount(false);
        try
        {
            await Connection.CloseAsync((long)Http3.Http3ErrorCode.NoError).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { /* best effort */ }
        finally
        {
            await Connection.DisposeAsync();
        }
    }
}
#pragma warning restore CA1416
