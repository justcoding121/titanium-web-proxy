using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Used to communicate with the server over HTTP(S)
/// </summary>
public class HttpWebClient
{
    private TcpServerConnection? connection;

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
            if (connection == null) throw new Exception("Connection is null");

            return connection;
        }
    }

    internal bool HasConnection => connection != null;

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
    ///     Override UpStreamEndPoint for this request; Local NIC via request is made
    /// </summary>
    public IPEndPoint? UpStreamEndPoint { get; set; }

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
    }

    /// <summary>
    ///     Prepare and send the http(s) request
    /// </summary>
    /// <returns></returns>
    internal async Task SendRequest(bool enable100ContinueBehaviour, bool isTransparent,
        OriginHttpVersionPolicy originHttpVersionPolicy, CancellationToken cancellationToken)
    {
        var upstreamProxy = Connection.UpStreamProxy;

        var useUpstreamProxy = upstreamProxy != null && upstreamProxy.ProxyType == ExternalProxyType.Http &&
                               !Connection.IsHttps;

        var serverStream = Connection.Stream;

        string? upstreamProxyUserName = null;
        string? upstreamProxyPassword = null;

        string url;
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
        else if (isTransparent)
        {
            url = Request.RequestUriString;
        }
        else
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
        var originHttpVersion = Request.HttpVersion;
        if (originHttpVersionPolicy == OriginHttpVersionPolicy.NormalizeToHttp11 &&
            originHttpVersion == HttpHeader.Version10)
        {
            originHttpVersion = HttpHeader.Version11;

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
        var headerBuilder = new HeaderBuilder();
        headerBuilder.WriteRequestLine(Request.Method, url, originHttpVersion);
        headerBuilder.WriteHeaders(Request.Headers, !isTransparent, upstreamProxyUserName, upstreamProxyPassword);

        // write request headers
        await serverStream.WriteHeadersAsync(headerBuilder, cancellationToken);

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

        ConnectRequest?.FinishSession();
        Request?.FinishSession();
        Response?.FinishSession();

        Data.Clear();
        UserData = null;
    }
}