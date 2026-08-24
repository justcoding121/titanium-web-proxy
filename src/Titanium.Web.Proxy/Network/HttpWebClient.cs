using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Used to communicate with the server over HTTP(S)
/// </summary>
public class HttpWebClient
{
    private TcpServerConnection? connection;

    /// <summary>
    ///     Upstream transport connection identity for multiplexed H2/H3 (and any path that binds an id
    ///     without transferring HTTP/1.1 TCP ownership via <see cref="SetConnection" />).
    /// </summary>
    private long? upstreamConnectionId;

    /// <summary>
    ///     Upstream peer endpoint paired with <see cref="upstreamConnectionId" /> for Bind-only
    ///     multiplexed sessions (H2/H3), so <c>ServerRemoteEndPoint</c> works without
    ///     <see cref="HasConnection" />.
    /// </summary>
    private IPEndPoint? upstreamRemoteEndPoint;

    /// <summary>
    ///     Establishment timing of the connection paired with <see cref="upstreamConnectionId" /> for
    ///     Bind-only multiplexed sessions (H2/H3), so <c>UpstreamConnectionTiming</c> works without
    ///     <see cref="HasConnection" />. Only populated when timing capture is enabled.
    /// </summary>
    private UpstreamConnectionTiming? upstreamConnectionTiming;

    internal HttpWebClient(ConnectRequest? connectRequest, Request request, Lazy<int> processIdFunc)
    {
        ConnectRequest = connectRequest;
        Request = request;
        Response = new Response();
        ProcessId = processIdFunc;
    }

    /// <summary>
    ///     Connection to server
    /// </summary>
    internal TcpServerConnection Connection
    {
        get
        {
            if (connection == null) throw new InvalidOperationException("Connection is null");

            return connection;
        }
    }

    internal bool HasConnection => connection != null;

    /// <summary>
    ///     Upstream connection identity when bound via <see cref="BindUpstreamConnection(TcpServerConnection)" />
    ///     or <see cref="SetConnection" />; otherwise <see langword="null" />.
    /// </summary>
    internal long? UpstreamConnectionId => upstreamConnectionId ?? connection?.Id;

    /// <summary>
    ///     Upstream peer endpoint when bound via <see cref="BindUpstreamConnection(TcpServerConnection)" />
    ///     or <see cref="SetConnection" />; otherwise <see langword="null" />.
    /// </summary>
    internal IPEndPoint? UpstreamRemoteEndPoint => upstreamRemoteEndPoint ?? connection?.RemoteEndPoint;

    /// <summary>
    ///     Establishment timing of the upstream connection when bound via
    ///     <see cref="BindUpstreamConnection(TcpServerConnection)" /> or <see cref="SetConnection" />;
    ///     <see langword="null" /> when timing capture is disabled or nothing is bound yet.
    /// </summary>
    internal UpstreamConnectionTiming? UpstreamConnectionTiming =>
        upstreamConnectionTiming ?? connection?.Timing;

    /// <summary>
    ///     Should we close the server connection at the end of this HTTP request/response session.
    /// </summary>
    internal bool CloseServerConnection { get; set; }

    /// <summary>
    ///     Stores internal data for the session.
    /// </summary>
    internal InternalDataStore Data { get; } = new();

    /// <summary>
    ///     Gets or sets the user data.
    /// </summary>
    public object? UserData { get; set; }

    /// <summary>
    ///     Override UpStreamEndPoint for this request; Local NIC via request is made.
    ///     Ignored for a destination whose address family does not match; prefer
    ///     <see cref="UpStreamEndPointIPv4" /> / <see cref="UpStreamEndPointIPv6" /> for dual-stack.
    /// </summary>
    public IPEndPoint? UpStreamEndPoint { get; set; }

    /// <summary>
    ///     Per-request local bind for IPv4 upstream destinations (overrides server
    ///     <c>UpStreamEndPointIPv4</c>).
    /// </summary>
    public IPEndPoint? UpStreamEndPointIPv4 { get; set; }

    /// <summary>
    ///     Per-request local bind for IPv6 upstream destinations (overrides server
    ///     <c>UpStreamEndPointIPv6</c>).
    /// </summary>
    public IPEndPoint? UpStreamEndPointIPv6 { get; set; }

    /// <summary>
    ///     Headers passed with Connect.
    /// </summary>
    public ConnectRequest? ConnectRequest { get; }

    /// <summary>
    ///     Web Request.
    /// </summary>
    public Request Request { get; }

    /// <summary>
    ///     Web Response.
    /// </summary>
    public Response Response { get; internal set; }

    /// <summary>
    ///     PID of the process that is created the current session when client is running in this machine
    ///     If client is remote then this will return
    /// </summary>
    public Lazy<int> ProcessId { get; internal set; }

    /// <summary>
    ///     Is Https?
    /// </summary>
    public bool IsHttps => Request.IsHttps;

    /// <summary>
    ///     Set the tcp connection to server used by this webclient
    /// </summary>
    /// <param name="serverConnection">Instance of <see cref="TcpServerConnection" /></param>
    internal void SetConnection(TcpServerConnection serverConnection)
    {
        serverConnection.LastAccess = DateTime.UtcNow;
        connection = serverConnection;
        // Overwrite any previously bound multiplexed (e.g. QUIC) metadata so TCP fallback is accurate.
        BindUpstreamConnection(serverConnection);
    }

    /// <summary>
    ///     Bind the metadata of a multiplexed HTTP/2 origin connection without transferring HTTP/1.1 TCP
    ///     stream ownership, so <c>ServerConnectionId</c>, <c>ServerRemoteEndPoint</c> and
    ///     <c>UpstreamConnectionTiming</c> are visible while <see cref="HasConnection" /> stays false
    ///     (H1 syphon/drain must not touch the shared socket).
    /// </summary>
    internal void BindUpstreamConnection(TcpServerConnection serverConnection)
    {
        BindUpstreamConnection(serverConnection.Id, serverConnection.RemoteEndPoint, serverConnection.Timing);
    }

    /// <summary>
    ///     Bind the metadata of a multiplexed QUIC (HTTP/3) origin connection. A later
    ///     <see cref="SetConnection" /> overwrites it, keeping TCP fallback accurate.
    /// </summary>
    internal void BindUpstreamConnection(QuicServerConnection quicConnection)
    {
        BindUpstreamConnection(quicConnection.Id, quicConnection.RemoteEndPoint, quicConnection.Timing);
    }

    /// <summary>
    ///     Snapshots upstream connection metadata by value. Read eagerly rather than through the
    ///     connection object because the socket/QUIC handle may be torn down before a consumer reads
    ///     <c>ServerRemoteEndPoint</c> in a later event.
    /// </summary>
    internal void BindUpstreamConnection(long id, IPEndPoint? remoteEndPoint,
        UpstreamConnectionTiming? timing)
    {
        upstreamConnectionId = id;
        upstreamRemoteEndPoint = remoteEndPoint;
        upstreamConnectionTiming = timing;
    }

    /// <summary>
    ///     Resolves the HTTP version that will actually be declared to the origin on the request line, per
    ///     <paramref name="originHttpVersionPolicy" />. Exposed so a caller that needs to know the outcome
    ///     <em>before</em> <see cref="SendRequest" /> writes headers - e.g. to decide whether
    ///     <c>Transfer-Encoding: chunked</c> must first be downgraded to buffered <c>Content-Length</c>
    ///     framing, since HTTP/1.0 has no chunked transfer-coding at all - does not have to re-derive this
    ///     logic and risk it drifting out of sync with what <see cref="SendRequest" /> actually sends.
    /// </summary>
    internal static Version ResolveOriginHttpVersion(Version requestHttpVersion,
        OriginHttpVersionPolicy originHttpVersionPolicy)
    {
        if (originHttpVersionPolicy == OriginHttpVersionPolicy.NormalizeToHttp11 &&
            requestHttpVersion == HttpHeader.Version10)
            return HttpHeader.Version11;

        return requestHttpVersion;
    }

    /// <summary>
    ///     Prepare and send the http(s) request
    /// </summary>
    /// <returns></returns>
    internal async Task SendRequest(bool enable100ContinueBehaviour, bool isTransparent, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        OriginHttpVersionPolicy originHttpVersionPolicy, CancellationToken cancellationToken)
    {
        var upstreamProxy = Connection.UpStreamProxy;

        var useUpstreamProxy = upstreamProxy != null && upstreamProxy.ProxyType == ExternalProxyType.Http &&
                               !Connection.IsHttps;

        var serverStream = Connection.Stream;

        string? upstreamProxyUserName = null;
        string? upstreamProxyPassword = null;

        string? url = null;
        if (useUpstreamProxy)
        {
            // Upstream HTTP proxies require absolute-form targets and may need Proxy-Authorization.
            // This applies to both explicit and transparent client-facing endpoints (#964): previously
            // the transparent branch skipped credential injection, so Basic-auth upstream proxies
            // returned 407 for plain HTTP even when UserName/Password were configured.
            //
            // Preserve the original request target verbatim rather than serialising through
            // System.Uri, which may normalise percent-encoding or drop non-ASCII characters.
            // Request.Url builds the absolute-form URL directly from the raw RequestUriString8
            // bytes (decoded/re-encoded via ISO-8859-1), so the upstream proxy receives exactly
            // what the client sent (or what a BeforeRequest handler wrote).
            url = Request.Url;

            if (!upstreamProxy!.UseDefaultCredentials &&
                !string.IsNullOrEmpty(upstreamProxy.UserName) && upstreamProxy.Password != null)
            {
                upstreamProxyUserName = upstreamProxy.UserName;
                upstreamProxyPassword = upstreamProxy.Password;
            }
        }
        else if (!isTransparent)
        {
            if (UriExtensions.GetScheme(Request.RequestUriString8).Length == 0)
                url = Request.RequestUriString;
            else
                url = Request.RequestUri.GetOriginalPathAndQuery();
        }

        if (url == string.Empty) url = "/";

        // The origin-bound wire version defaults to whatever the client itself declared (pass-through); it is
        // never written back onto Request.HttpVersion itself, which stays the client-facing version that event
        // handlers observe (see OriginHttpVersionPolicy).
        var originHttpVersion = ResolveOriginHttpVersion(Request.HttpVersion, originHttpVersionPolicy);
        if (originHttpVersion == HttpHeader.Version11 && Request.HttpVersion == HttpHeader.Version10)
        {
            // Some origins that are only conditionally HTTP/1.1-compliant still mirror the persistence implied
            // by whatever version/headers they were sent rather than trusting HTTP/1.1's persistent-by-default
            // rule; explicitly asking for "keep-alive" maximizes compatibility with that class of origin. An
            // explicit client "Connection: close" is still honored verbatim - normalizing the origin-facing
            // version never overrides an explicit request to close.
            var connectionHeader = Request.Headers.GetHeaderValueOrNull(KnownHeaders.Connection);
            if (connectionHeader == null ||
                connectionHeader.EqualsIgnoreCase(KnownHeaders.ConnectionKeepAlive.String))
                Request.Headers.SetOrAddHeaderValue(KnownHeaders.Connection,
                    KnownHeaders.ConnectionKeepAlive.String);
        }

        // prepare the request & headers
        var headerBuilder = HeaderBuilder.Rent();
        try
        {
            if (url != null)
                headerBuilder.WriteRequestLine(Request.Method, url, originHttpVersion);
            else
                headerBuilder.WriteRequestLine(Request.Method, Request.RequestUriString8, originHttpVersion);
            // Transparent reverse: strip hop-by-hop Connection: close so NC clients do not force
            // the origin to close (keeps the upstream pool warm). HTTP/1.0 Connection: keep-alive
            // is left intact — 1.0 is non-persistent unless the origin sees that opt-in.
            if (isTransparent)
                Request.StripHopByHopConnectionForTransparentOrigin();
            headerBuilder.WriteHeaders(Request.Headers, !isTransparent, upstreamProxyUserName, upstreamProxyPassword);

            // write request headers
            await serverStream.WriteHeadersAsync(headerBuilder, cancellationToken);
        }
        finally
        {
            HeaderBuilder.Return(headerBuilder);
        }

        if (enable100ContinueBehaviour && Request.ExpectContinue)
        {
            // wait for expectation response from server
            await ReceiveResponse(cancellationToken);

            if (Response.StatusCode == (int)HttpStatusCode.Continue)
                Request.ExpectationSucceeded = true;
            else
                Request.ExpectationFailed = true;
        }
    }

    /// <summary>
    ///     Receive and parse the http response from server
    /// </summary>
    /// <returns></returns>
    internal async Task ReceiveResponse(CancellationToken cancellationToken)
    {
        // return if this is already read
        if (Response.StatusCode != 0) return;

        Response.RequestMethod = Request.Method;

        var httpStatus = await Connection.Stream.ReadResponseStatus(cancellationToken);
        if (httpStatus == null)
        {
            // EOF before any response bytes: typically a stale pooled keep-alive connection.
            // RetryPolicy re-runs the whole exchange; only safe when there is no body or the
            // body is buffered in memory (IsBodyRead). A streamed body cannot be replayed.
            if (!Request.HasBody || Request.IsBodyRead)
                throw new RetryableServerConnectionException(
                    "Server connection was closed before any response was received.");

            throw new IOException("Server closed the connection before sending a response.");
        }

        Response.HttpVersion = httpStatus.Value.Version;
        Response.StatusCode = httpStatus.Value.StatusCode;
        Response.StatusDescription = httpStatus.Value.Description;

        // Read the response headers in to unique and non-unique header collections
        await HeaderParser.ReadHeaders(Connection.Stream, Response.Headers, cancellationToken);
    }

    /// <summary>
    ///     Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    internal void FinishSession()
    {
        connection = null;
        upstreamConnectionId = null;
        upstreamRemoteEndPoint = null;
        upstreamConnectionTiming = null;

        ConnectRequest?.FinishSession();
        Request?.FinishSession();
        Response?.FinishSession();

        Data.Clear();
        UserData = null;
    }

    /// <summary>
    ///     Unbinds the origin socket and resets request/response in place for the next keep-alive GET.
    /// </summary>
    internal void ResetForKeepAlive()
    {
        connection = null;
        upstreamConnectionId = null;
        upstreamRemoteEndPoint = null;
        upstreamConnectionTiming = null;
        CloseServerConnection = false;
        Request.ResetForKeepAlive();
        Response.ResetForKeepAlive();
        Data.Clear();
        UserData = null;
    }

}