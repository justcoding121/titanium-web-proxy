using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy;

/// <summary>
///     Handle the request
/// </summary>
public partial class ProxyServer
{
    /// <summary>
    ///     This is the core request handler method for a particular connection from client.
    ///     Will create new session (request/response) sequence until
    ///     client/server abruptly terminates connection or by normal HTTP termination.
    /// </summary>
    /// <param name="endPoint">The proxy endpoint.</param>
    /// <param name="clientStream">The client stream.</param>
    /// <param name="cancellationTokenSource">The cancellation token source for this async task.</param>
    /// <param name="connectArgs">The Connect request if this is a HTTPS request from explicit endpoint.</param>
    /// <param name="prefetchConnectionTask">Prefetched server connection for current client using Connect/SNI headers.</param>
    /// <param name="isHttps">Is HTTPS</param>
    private async Task HandleHttpSessionRequest(ProxyEndPoint endPoint, HttpClientStream clientStream,
        CancellationTokenSource cancellationTokenSource, TunnelConnectSessionEventArgs? connectArgs = null,
        Task<TcpServerConnection?>? prefetchConnectionTask = null, bool isHttps = false)
    {
        var connectRequest = connectArgs?.HttpClient.ConnectRequest;

        var prefetchTask = prefetchConnectionTask;
        TcpServerConnection? connection = null;
        var closeServerConnection = false;

        try
        {
            var cancellationToken = cancellationTokenSource.Token;

            // Loop through each subsequent request on this particular client connection
            // (assuming HTTP connection is kept alive by client)
            while (true)
            {
                if (clientStream.IsClosed) return;

                // read the request line
                var requestLine = await clientStream.ReadRequestLine(cancellationToken);
                if (requestLine.IsEmpty()) return;

                var args = new SessionEventArgs(this, endPoint, clientStream, connectRequest, cancellationTokenSource)
                {
                    UserData = connectArgs?.UserData
                };

                var request = args.HttpClient.Request;
                if (isHttps) request.IsHttps = true;

                try
                {
                    try
                    {
                        // Read the request headers in to unique and non-unique header collections
                        await HeaderParser.ReadHeaders(clientStream, args.HttpClient.Request.Headers,
                            cancellationToken);

                        if (connectRequest != null)
                        {
                            request.IsHttps = connectRequest.IsHttps;
                            request.Authority = connectRequest.Authority;
                        }

                        request.RequestUriString8 = requestLine.RequestUri;

                        request.Method = requestLine.Method;
                        request.HttpVersion = requestLine.Version;

                        // we need this to syphon out data from connection if API user changes them.
                        request.SetOriginalHeaders();

                        // If user requested interception do it
                        await OnBeforeRequest(args);

                        if (!args.IsTransparent && !args.IsSocks)
                        {
                            // proxy authorization check
                            if (connectRequest == null && await CheckAuthorization(args) == false)
                            {
                                await OnBeforeResponse(args);

                                // send the response
                                await clientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
                                return;
                            }

                            PrepareRequestHeaders(request.Headers);
                            request.Host = request.RequestUri.Authority;
                        }

                        // if win auth is enabled
                        // we need a cache of request body
                        // so that we can send it after authentication in WinAuthHandler.cs
                        if (args.EnableWinAuth && request.HasBody) await args.GetRequestBody(cancellationToken);

                        var response = args.HttpClient.Response;

                        if (request.CancelRequest)
                        {
                            if (!(Enable100ContinueBehaviour && request.ExpectContinue))
                                // syphon out the request body from client before setting the new body
                                await args.SyphonOutBodyAsync(true, cancellationToken);

                            await HandleHttpSessionResponse(args);

                            if (!response.KeepAlive) return;

                            continue;
                        }

                        // If prefetch task is available.
                        if (connection == null && prefetchTask != null)
                        {
                            try
                            {
                                connection = await prefetchTask;
                            }
                            catch (SocketException e)
                            {
                                if (e.SocketErrorCode != SocketError.HostNotFound) throw;
                            }

                            prefetchTask = null;
                        }

                        if (connection != null)
                        {
                            var socket = connection.TcpSocket;
                            var part1 = socket.Poll(1000, SelectMode.SelectRead);
                            var part2 = socket.Available == 0;
                            if (part1 & part2)
                            {
                                //connection is closed
                                await TcpConnectionFactory.Release(connection, true);
                                connection = null;
                            }
                        }

                        // create a new connection if cache key changes.
                        // only gets hit when connection pool is disabled.
                        // or when prefetch task has a unexpectedly different connection.
                        if (connection != null
                            && await TcpConnectionFactory.GetConnectionCacheKey(this, args,
                                clientStream.Connection.NegotiatedApplicationProtocol)
                            != connection.CacheKey)
                        {
                            await TcpConnectionFactory.Release(connection);
                            connection = null;
                        }

                        var result = await HandleHttpSessionRequest(args, connection,
                            clientStream.Connection.NegotiatedApplicationProtocol,
                            cancellationToken, cancellationTokenSource);

                        var newConnection = result.LatestConnection;
                        if (connection != newConnection && connection != null)
                            await TcpConnectionFactory.Release(connection);

                        // update connection to latest used
                        connection = result.LatestConnection;

                        closeServerConnection = !result.Continue;

                        // throw if exception happened
                        if (result.Exception != null) throw result.Exception;

                        if (!result.Continue) return;

                        // user requested
                        if (args.HttpClient.CloseServerConnection)
                        {
                            closeServerConnection = true;
                            return;
                        }

                        // if connection is closing exit
                        if (!response.KeepAlive)
                        {
                            closeServerConnection = true;
                            return;
                        }

                        if (cancellationTokenSource.IsCancellationRequested)
                            throw new Exception("Session was terminated by user.");

                        // Release server connection for each HTTP session instead of per client connection.
                        // This will be more efficient especially when client is idly holding server connection 
                        // between sessions without using it.
                        // Do not release authenticated connections for performance reasons.
                        // Otherwise it will keep authenticating per session.
                        if (EnableConnectionPool && connection != null
                                                 && !connection.IsWinAuthenticated)
                        {
                            await TcpConnectionFactory.Release(connection);
                            connection = null;
                        }
                    }
                    catch (Exception e) when (!(e is ProxyHttpException))
                    {
                        throw new ProxyHttpException("Error occured whilst handling session request", e, args);
                    }
                }
                catch (Exception e)
                {
                    args.Exception = e;
                    closeServerConnection = true;
                    throw;
                }
                finally
                {
                    await OnAfterResponse(args);
                    args.Dispose();
                }
            }
        }
        finally
        {
            if (connection != null) await TcpConnectionFactory.Release(connection, closeServerConnection);

            await TcpConnectionFactory.Release(prefetchTask, closeServerConnection);
        }
    }

    private async Task<RetryResult> HandleHttpSessionRequest(SessionEventArgs args,
        TcpServerConnection? serverConnection, SslApplicationProtocol sslApplicationProtocol,
        CancellationToken cancellationToken, CancellationTokenSource cancellationTokenSource)
    {
        args.HttpClient.Request.Locked = true;

        // do not cache server connections for WebSockets
        var noCache = args.HttpClient.Request.UpgradeToWebSocket;

        if (noCache) serverConnection = null;

        // a connection generator task with captured parameters via closure.
        var generator = () =>
            TcpConnectionFactory.GetServerConnection(this,
                args,
                false,
                sslApplicationProtocol,
                noCache,
                cancellationToken);

        /// Retry with new connection if the initial stream.WriteAsync call to server fails.
        /// i.e if request line and headers failed to get send.
        /// Do not retry after reading data from client stream, 
        /// because subsequent try will not have data to read from client 
        /// and will hang at clientStream.ReadAsync call.
        /// So, throw RetryableServerConnectionException only when we are sure we can retry safely.
        return await RetryPolicy<RetryableServerConnectionException>().ExecuteAsync(async connection =>
        {
            // set the connection and send request headers
            args.HttpClient.SetConnection(connection);

            args.TimeLine["Connection Ready"] = DateTime.UtcNow;

            if (args.HttpClient.Request.UpgradeToWebSocket)
            {
                // connectRequest can be null for SOCKS connection
                if (args.HttpClient.ConnectRequest != null)
                    args.HttpClient.ConnectRequest!.TunnelType = TunnelType.Websocket;

                // if upgrading to websocket then relay the request without reading the contents
                await HandleWebSocketUpgrade(args, args.ClientStream, connection, cancellationTokenSource,
                    cancellationToken);
                return false;
            }

            // construct the web request that we are going to issue on behalf of the client.
            await HandleHttpSessionRequest(args);
            return true;
        }, generator, serverConnection);
    }

    private async Task HandleHttpSessionRequest(SessionEventArgs args)
    {
        var cancellationToken = args.CancellationTokenSource.Token;
        var request = args.HttpClient.Request;

        var body = request.CompressBodyAndUpdateContentLength();

        await args.HttpClient.SendRequest(Enable100ContinueBehaviour, args.IsTransparent,
            cancellationToken);

        // If a successful 100 continue request was made, inform that to the client and reset response
        if (request.ExpectationSucceeded)
        {
            var writer = args.ClientStream;
            var response = args.HttpClient.Response;

            var headerBuilder = new HeaderBuilder();
            headerBuilder.WriteResponseLine(response.HttpVersion, response.StatusCode, response.StatusDescription);
            headerBuilder.WriteHeaders(response.Headers);
            await writer.WriteHeadersAsync(headerBuilder, cancellationToken);

            await args.ClearResponse(cancellationToken);
        }

        // send body to server if available
        if (request.HasBody)
        {
            if (request.IsBodyRead)
                await args.HttpClient.Connection.Stream.WriteBodyAsync(body!, request.IsChunked, cancellationToken);
            else if (!request.ExpectationFailed)
                // get the request body unless an unsuccessful 100 continue request was made
                await args.CopyRequestBodyAsync(args.HttpClient.Connection.Stream, TransformationMode.None,
                    cancellationToken);
        }

        args.TimeLine["Request Sent"] = DateTime.UtcNow;

        // parse and send response
        await HandleHttpSessionResponse(args);
    }

    /// <summary>
    ///     Prepare the request headers so that we can avoid encodings not parseable by this proxy
    /// </summary>
    private void PrepareRequestHeaders(HeaderCollection requestHeaders)
    {
        var acceptEncoding = requestHeaders.GetHeaderValueOrNull(KnownHeaders.AcceptEncoding);

        if (acceptEncoding != null)
        {
            var supportedAcceptEncoding = new List<string>();

            // only allow proxy supported compressions
            supportedAcceptEncoding.AddRange(acceptEncoding.Split(',')
                .Select(x => x.Trim())
                .Where(x => ProxyConstants.ProxySupportedCompressions.Contains(x)));

            // uncompressed is always supported by proxy
            supportedAcceptEncoding.Add("identity");

            requestHeaders.SetOrAddHeaderValue(KnownHeaders.AcceptEncoding,
                string.Join(", ", supportedAcceptEncoding));
        }

        requestHeaders.FixProxyHeaders();
    }

    /// <summary>
    ///     Invoke before request handler if it is set.
    /// </summary>
    /// <param name="args">The session event arguments.</param>
    /// <returns></returns>
    private async Task OnBeforeRequest(SessionEventArgs args)
    {
        args.TimeLine["Request Received"] = DateTime.UtcNow;

        if (BeforeRequest != null) await BeforeRequest.InvokeAsync(this, args, ExceptionFunc);
    }

    /// <summary>
    ///     Invoke before request handler if it is set.
    /// </summary>
    /// <param name="request">The COONECT request.</param>
    /// <returns></returns>
    internal async Task OnBeforeUpStreamConnectRequest(ConnectRequest request)
    {
        if (BeforeUpStreamConnectRequest != null)
            await BeforeUpStreamConnectRequest.InvokeAsync(this, request, ExceptionFunc);
    }

#if DEBUG
        internal bool ShouldCallBeforeRequestBodyWrite()
        {
            if (OnRequestBodyWrite != null)
            {
                return true;
            }

            return false;
        }

        internal async Task OnBeforeRequestBodyWrite(BeforeBodyWriteEventArgs args)
        {
            if (OnRequestBodyWrite != null)
            {
                await OnRequestBodyWrite.InvokeAsync(this, args, ExceptionFunc);
            }
        }
#endif
}