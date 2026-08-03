using System;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     Holds info related to a single proxy session (single request/response exchange).
///     Under HTTP/2 and HTTP/3, many sessions share one client connection (one stream each);
///     ending a session ends that request/response exchange, not necessarily the connection.
/// </summary>
public abstract class SessionEventArgsBase : ProxyEventArgsBase, IDisposable
{
    protected readonly IBufferPool BufferPool;

    internal readonly CancellationTokenSource CancellationTokenSource;

    /// <summary>
    ///     Optional per-request token (e.g. linked request-timeout deadline). When set, handlers should
    ///     prefer <see cref="CancellationToken" /> over <see cref="CancellationTokenSource" />.Token alone.
    /// </summary>
    internal CancellationToken? OperationCancellationToken { get; set; }

    /// <summary>
    ///     Effective cancellation token for the current request exchange.
    /// </summary>
    internal CancellationToken CancellationToken => OperationCancellationToken ?? CancellationTokenSource.Token;

    /// <summary>
    ///     The single registry every <see cref="Helpers.DeadlineRegistry.Deadline" /> composed for this
    ///     session's request/response exchange is started against, so a firing recorded deep in one
    ///     handler (e.g. an idle-write stall) is still attributable by a catch block in a different
    ///     handler several layers up with no <see cref="Helpers.DeadlineRegistry.Deadline" /> of its own
    ///     in between - see <see cref="Helpers.DeadlineRegistry" />'s remarks for why that matters.
    /// </summary>
    internal DeadlineRegistry Deadlines { get; } = new();

    private bool disposed;
    private bool enableWinAuth;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SessionEventArgsBase" /> class.
    /// </summary>
    private protected SessionEventArgsBase(ProxyServer server, ProxyEndPoint endPoint,
        HttpClientStream clientStream, ConnectRequest? connectRequest, Request request,
        CancellationTokenSource cancellationTokenSource) : base(server, clientStream.Connection)
    {
        BufferPool = server.BufferPool;

        var sessionCreatedAt = DateTime.UtcNow;
        Timing = server.EnableRequestTimingCapture ? new HttpRequestTiming(sessionCreatedAt) : null;

        CancellationTokenSource = cancellationTokenSource;

        ClientStream = clientStream;
        HttpClient = new HttpWebClient(connectRequest, request,
            new Lazy<int>(() => clientStream.Connection.GetProcessId(endPoint)));
        ProxyEndPoint = endPoint;
        EnableWinAuth = server.EnableWinAuth && IsWindowsAuthenticationSupported;
    }

    private static bool IsWindowsAuthenticationSupported => RunTime.IsWindows;

    internal TcpServerConnection ServerConnection => HttpClient.Connection;

    /// <summary>
    ///     Holds a reference to client
    /// </summary>
    internal TcpClientConnection ClientConnection => ClientStream.Connection;

    internal HttpClientStream ClientStream { get; }

    /// <summary>
    ///     Identity of the inbound client transport connection. Multiplexed HTTP/2 and HTTP/3 streams
    ///     that share one client connection expose the same value. Values are process-wide monotonic
    ///     counters starting at 1 (wrapping back to 1 after <see cref="long.MaxValue" />).
    /// </summary>
    public long ClientConnectionId => ClientConnection.Id;

    /// <summary>
    ///     Identity of the upstream origin transport connection when one has been acquired for this
    ///     session; otherwise <c>0</c>. Multiplexed HTTP/2 and HTTP/3 sessions that share one origin
    ///     connection expose the same value. This does not imply per-session pool ownership of that
    ///     connection. Values are process-wide monotonic counters starting at 1 (wrapping back to 1
    ///     after <see cref="long.MaxValue" />).
    /// </summary>
    public long ServerConnectionId => HttpClient.UpstreamConnectionId ?? 0;

    /// <summary>
    ///     Structured timing for this session's request/response exchange, populated only when
    ///     <see cref="ProxyServer.EnableRequestTimingCapture" /> is enabled (otherwise <see langword="null" />
    ///     and no timing overhead is incurred anywhere in the proxy). See <see cref="HttpRequestTiming" />.
    /// </summary>
    public HttpRequestTiming? Timing { get; }

    /// <summary>
    ///     Structured timing for the upstream connection currently used by this session, populated only
    ///     when <see cref="ProxyServer.EnableRequestTimingCapture" /> is enabled, including multiplexed
    ///     HTTP/2 and HTTP/3 sessions that bind identity without transferring HTTP/1.1 TCP ownership
    ///     (those sharing one origin connection expose the same instance). <see langword="null" /> when
    ///     timing capture is disabled or no upstream connection has been acquired yet (e.g. the request
    ///     was answered synthetically). See <see cref="UpstreamConnectionTiming" />.
    /// </summary>
    public UpstreamConnectionTiming? UpstreamConnectionTiming => HttpClient.UpstreamConnectionTiming;

    /// <summary>
    ///     Returns a user data for this request/response session which is
    ///     same as the user data of HttpClient.
    /// </summary>
    public object? UserData
    {
        get => HttpClient.UserData;
        set => HttpClient.UserData = value;
    }

    /// <summary>
    ///     Per-session override for <see cref="ProxyServer.ConnectTimeOutSeconds" />.
    ///     <see langword="null" /> uses the server default; <see cref="TimeSpan.Zero" /> or negative
    ///     disables the connect timeout. Set in <c>BeforeRequest</c> to speed up or slow down the
    ///     TCP connect race for this individual request.
    /// </summary>
    public TimeSpan? ConnectTimeout { get; set; }

    /// <summary>
    ///     Enable/disable Windows Authentication (NTLM/Kerberos) for the current session.
    /// </summary>
    public bool EnableWinAuth
    {
        get => enableWinAuth;
        set
        {
            if (value && !IsWindowsAuthenticationSupported)
                throw new NotSupportedException("Windows Authentication is not supported");

            enableWinAuth = value;
        }
    }

    /// <summary>
    ///     Does this session uses SSL?
    /// </summary>
    public bool IsHttps => HttpClient.Request.IsHttps;

    /// <summary>
    ///     Client Local End Point.
    /// </summary>
    public IPEndPoint ClientLocalEndPoint => (IPEndPoint)ClientConnection.LocalEndPoint;

    /// <summary>
    ///     Client Remote End Point.
    /// </summary>
    public IPEndPoint ClientRemoteEndPoint => (IPEndPoint)ClientConnection.RemoteEndPoint;

    [Obsolete("Use ClientRemoteEndPoint instead.")]
    public IPEndPoint ClientEndPoint => ClientRemoteEndPoint;

    /// <summary>
    ///     Physical peer of the established upstream connection (no second DNS lookup).
    ///     Available after the server connection is established (for example in
    ///     <see cref="ProxyServer.BeforeResponse" />), including multiplexed HTTP/2 and HTTP/3
    ///     sessions that bind identity without transferring HTTP/1.1 TCP ownership. When an
    ///     upstream HTTP/SOCKS proxy is used, this is the proxy hop endpoint, not the origin
    ///     server. <see langword="null" /> when no upstream connection exists (for example a
    ///     synthetic local response).
    /// </summary>
    public IPEndPoint? ServerRemoteEndPoint => HttpClient.UpstreamRemoteEndPoint;

    /// <summary>
    ///     IP address of <see cref="ServerRemoteEndPoint" />.
    /// </summary>
    public IPAddress? ServerIpAddress => ServerRemoteEndPoint?.Address;

    /// <summary>
    ///     The web client used to communicate with server for this session.
    /// </summary>
    public HttpWebClient HttpClient { get; }

    [Obsolete("Use HttpClient instead.")] public HttpWebClient WebSession => HttpClient;

    /// <summary>
    ///     Gets or sets the custom up stream proxy.
    /// </summary>
    /// <value>
    ///     The custom up stream proxy.
    /// </value>
    public IExternalProxy? CustomUpStreamProxy { get; set; }

    /// <summary>
    ///     Are we using a custom upstream HTTP(S) proxy?
    /// </summary>
    public IExternalProxy? CustomUpStreamProxyUsed { get; internal set; }

    /// <summary>
    ///     Local endpoint via which we make the request.
    /// </summary>
    public ProxyEndPoint ProxyEndPoint { get; }

    [Obsolete("Use ProxyEndPoint instead.")]
    public ProxyEndPoint LocalEndPoint => ProxyEndPoint;

    /// <summary>
    ///     Is this a transparent endpoint (TCP or QUIC)?
    /// </summary>
    public bool IsTransparent => ProxyEndPoint is TransparentBaseProxyEndPoint;

    /// <summary>
    ///     Is this a SOCKS endpoint?
    /// </summary>
    public bool IsSocks => ProxyEndPoint is SocksProxyEndPoint;

    /// <summary>
    ///     The last exception that happened.
    /// </summary>
    public Exception? Exception { get; internal set; }

    /// <summary>
    ///     True once any HTTP response status/headers have been written to the client for this session.
    ///     Used to decide whether a timeout may still safely inject HTTP 504.
    /// </summary>
    internal bool IsClientResponseCommitted { get; set; }

    /// <summary>
    ///     The live logger for the <see cref="ProxyServer" /> that owns this session. Always reads the
    ///     server's current logger rather than a value snapshotted at session creation, so a logger
    ///     replaced via <see cref="ProxyServer.ApplyLoggingConfiguration" /> is picked up immediately.
    /// </summary>
    protected ILogger Logger => Server.Logger;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected void OnException(Exception exception)
    {
        ProxyDiagnostics.ReportException(Logger, "Unhandled exception in proxy session", exception);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        // No finalizer: session objects only own managed state. Explicit Dispose clears
        // handlers and finishes the HTTP client; omitting Dispose leaves no unmanaged leak.
        if (disposing)
        {
            CustomUpStreamProxyUsed = null;

            HttpClient.FinishSession();

            DataSent = null;
            DataReceived = null;
            Exception = null;
        }

        disposed = true;
    }

    /// <summary>
    ///     Fired when data is sent within this session to server/client.
    /// </summary>
    public event EventHandler<DataEventArgs>? DataSent;

    /// <summary>
    ///     Fired when data is received within this session from client/server.
    /// </summary>
    public event EventHandler<DataEventArgs>? DataReceived;

    /// <summary>
    ///     True if a raw byte-level tap (<see cref="DataSent"/> or <see cref="DataReceived"/>) is
    ///     subscribed. Used by the WebSocket upgrade handler: a subscriber typically decodes the raw
    ///     bytes as WebSocket frames (e.g. via <c>WebSocketDecoder</c>), which - like frame-level
    ///     interception - cannot handle RSV-flagged frames produced by extensions such as
    ///     permessage-deflate.
    /// </summary>
    internal bool HasWebSocketDataTapHandler => DataSent != null || DataReceived != null;

    internal void OnDataSent(byte[] buffer, int offset, int count)
    {
        try
        {
            DataSent?.Invoke(this, new DataEventArgs(buffer, offset, count));
        }
        catch (Exception ex)
        {
            OnException(new Exception("Exception thrown in user event", ex));
        }
    }

    internal void OnDataReceived(byte[] buffer, int offset, int count)
    {
        try
        {
            DataReceived?.Invoke(this, new DataEventArgs(buffer, offset, count));
        }
        catch (Exception ex)
        {
            OnException(new Exception("Exception thrown in user event", ex));
        }
    }

    /// <summary>
    ///     Terminates the session abruptly by terminating client/server connections.
    /// </summary>
    public void TerminateSession()
    {
        CancellationTokenSource.Cancel();
    }
}