using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
#if NETCOREAPP2_1
using System.Net.Security;
#endif
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended.Network;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy
{
    /// <summary>
    ///     Handle the request
    /// </summary>
    public partial class ProxyServer
    {
        private bool isWindowsAuthenticationEnabledAndSupported =>
            EnableWinAuth && RunTime.IsWindows && !RunTime.IsRunningOnMono;

        /// <summary>
        ///     This is the core request handler method for a particular connection from client.
        ///     Will create new session (request/response) sequence until
        ///     client/server abruptly terminates connection or by normal HTTP termination.
        /// </summary>
        /// <param name="endPoint">The proxy endpoint.</param>
        /// <param name="clientConnection">The client connection.</param>
        /// <param name="clientStream">The client stream.</param>
        /// <param name="clientStreamWriter">The client stream writer.</param>
        /// <param name="cancellationTokenSource">The cancellation token source for this async task.</param>
        /// <param name="httpsConnectHostname">
        ///     The https hostname as appeared in CONNECT request if this is a HTTPS request from
        ///     explicit endpoint.
        /// </param>
        /// <param name="connectRequest">The Connect request if this is a HTTPS request from explicit endpoint.</param>
        /// <param name="prefetchConnectionTask">Prefetched server connection for current client using Connect/SNI headers.</param>
        private async Task handleHttpSessionRequest(ProxyEndPoint endPoint, TcpClientConnection clientConnection,
            CustomBufferedStream clientStream, HttpResponseWriter clientStreamWriter,
            CancellationTokenSource cancellationTokenSource, string httpsConnectHostname, ConnectRequest connectRequest,
            Task<TcpServerConnection> prefetchConnectionTask = null)
        {
            var prefetchTask = prefetchConnectionTask;
            TcpServerConnection connection = null;
            bool closeServerConnection = false;

            try
            {
                var cancellationToken = cancellationTokenSource.Token;

                // Loop through each subsequest request on this particular client connection
                // (assuming HTTP connection is kept alive by client)
                while (true)
                {
                    // read the request line
                    string httpCmd = await clientStream.ReadLineAsync(cancellationToken);

                    if (string.IsNullOrEmpty(httpCmd))
                    {
                        return;
                    }

                    var args = new SessionEventArgs(this, endPoint, cancellationTokenSource)
                    {
                        ProxyClient = { Connection = clientConnection },
                        HttpClient = { ConnectRequest = connectRequest }
                    };

                    try
                    {
                        try
                        {
                            Request.ParseRequestLine(httpCmd, out string httpMethod, out string httpUrl,
                                out var version);

                            // Read the request headers in to unique and non-unique header collections
                            await HeaderParser.ReadHeaders(clientStream, args.HttpClient.Request.Headers,
                                cancellationToken);

                            Uri httpRemoteUri;
                            if (ProxyConstants.UriSchemeRegex.IsMatch(httpUrl))
                            {
                                try
                                {
                                    httpRemoteUri = new Uri(httpUrl);
                                }
                                catch (Exception ex)
                                {
                                    throw new Exception($"Invalid URI: '{httpUrl}'", ex);
                                }
                            }
                            else
                            {
                                string host = args.HttpClient.Request.Host ?? httpsConnectHostname;
                                string hostAndPath = host;
                                if (httpUrl.StartsWith("/"))
                                {
                                    hostAndPath += httpUrl;
                                }

                                string url = string.Concat(httpsConnectHostname == null ? "http://" : "https://",
                                    hostAndPath);
                                try
                                {
                                    httpRemoteUri = new Uri(url);
                                }
                                catch (Exception ex)
                                {
                                    throw new Exception($"Invalid URI: '{url}'", ex);
                                }
                            }

                            var request = args.HttpClient.Request;
                            request.RequestUri = httpRemoteUri;
                            request.OriginalUrl = httpUrl;

                            request.Method = httpMethod;
                            request.HttpVersion = version;
                            args.ProxyClient.ClientStream = clientStream;
                            args.ProxyClient.ClientStreamWriter = clientStreamWriter;

                            if (!args.IsTransparent)
                            {
                                // proxy authorization check
                                if (httpsConnectHostname == null && await checkAuthorization(args) == false)
                                {
                                    await invokeBeforeResponse(args);

                                    // send the response
                                    await clientStreamWriter.WriteResponseAsync(args.HttpClient.Response,
                                        cancellationToken: cancellationToken);
                                    return;
                                }

                                prepareRequestHeaders(request.Headers);
                                request.Host = request.RequestUri.Authority;
                            }

                            // if win auth is enabled
                            // we need a cache of request body
                            // so that we can send it after authentication in WinAuthHandler.cs
                            if (isWindowsAuthenticationEnabledAndSupported && request.HasBody)
                            {
                                await args.GetRequestBody(cancellationToken);
                            }

                            //we need this to syphon out data from connection if API user changes them.
                            request.SetOriginalHeaders();

                            args.TimeLine["Request Received"] = DateTime.Now;

                            // If user requested interception do it
                            await invokeBeforeRequest(args);

                            var response = args.HttpClient.Response;

                            if (request.CancelRequest)
                            {
                                // syphon out the request body from client before setting the new body
                                await args.SyphonOutBodyAsync(true, cancellationToken);

                                await handleHttpSessionResponse(args);

                                if (!response.KeepAlive)
                                {
                                    return;
                                }

                                continue;
                            }

                            //If prefetch task is available.
                            if (connection == null && prefetchTask != null)
                            {
                                connection = await prefetchTask;
                                prefetchTask = null;
                            }

                            // create a new connection if cache key changes.
                            // only gets hit when connection pool is disabled.
                            // or when prefetch task has a unexpectedly different connection.
                            if (connection != null
                                && (await tcpConnectionFactory.GetConnectionCacheKey(this, args,
                                    clientConnection.NegotiatedApplicationProtocol)
                                                != connection.CacheKey))
                            {
                                await tcpConnectionFactory.Release(connection);
                                connection = null;
                            }

                            var result = await handleHttpSessionRequest(httpCmd, args, connection,
                                  clientConnection.NegotiatedApplicationProtocol,
                                  cancellationToken, cancellationTokenSource);

                            //update connection to latest used
                            connection = result.LatestConnection;
                            closeServerConnection = !result.Continue;           

                            //throw if exception happened
                            if (!result.IsSuccess)
                            {
                                throw result.Exception;
                            }

                            if (!result.Continue)
                            {
                                return;
                            }

                            //user requested
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
                            {
                                throw new Exception("Session was terminated by user.");
                            }

                            //Release server connection for each HTTP session instead of per client connection.
                            //This will be more efficient especially when client is idly holding server connection 
                            //between sessions without using it.
                            //Do not release authenticated connections for performance reasons.
                            //Otherwise it will keep authenticating per session.
                            if (EnableConnectionPool && connection != null
                                    && !connection.IsWinAuthenticated)
                            {
                                await tcpConnectionFactory.Release(connection);
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
                        await invokeAfterResponse(args);
                        args.Dispose();
                    }
                }
            }
            finally
            {
                await tcpConnectionFactory.Release(connection,
                        closeServerConnection);

                await tcpConnectionFactory.Release(prefetchTask, closeServerConnection);
            }
        }

        private async Task<RetryResult> handleHttpSessionRequest(string httpCmd, SessionEventArgs args,
          TcpServerConnection serverConnection, SslApplicationProtocol sslApplicationProtocol,
          CancellationToken cancellationToken, CancellationTokenSource cancellationTokenSource)
        {
            //a connection generator task with captured parameters via closure.
            Func<Task<TcpServerConnection>> generator = () =>
                            tcpConnectionFactory.GetServerConnection(this, args, isConnect: false,
                                    applicationProtocol: sslApplicationProtocol,
                                    noCache: false, cancellationToken: cancellationToken);

            //for connection pool, retry fails until cache is exhausted.   
            return await retryPolicy<ServerConnectionException>().ExecuteAsync(async (connection) =>
            {
                args.TimeLine["Connection Ready"] = DateTime.Now;

                if (args.HttpClient.Request.UpgradeToWebSocket)
                {
                    // if upgrading to websocket then relay the request without reading the contents
                    await handleWebSocketUpgrade(httpCmd, args, args.HttpClient.Request,
                        args.HttpClient.Response, args.ProxyClient.ClientStream, args.ProxyClient.ClientStreamWriter,
                        connection, cancellationTokenSource, cancellationToken);
                    return false;
                }

                args.TimeLine["Connection Ready"] = DateTime.Now;

                // construct the web request that we are going to issue on behalf of the client.
                await handleHttpSessionRequest(connection, args);
                return true;

            }, generator, serverConnection);
        }

        private async Task handleHttpSessionRequest(TcpServerConnection connection, SessionEventArgs args)
        {
            var cancellationToken = args.CancellationTokenSource.Token;
            var request = args.HttpClient.Request;
            request.Locked = true;

            var body = request.CompressBodyAndUpdateContentLength();

            // if expect continue is enabled then send the headers first 
            // and see if server would return 100 conitinue
            if (request.ExpectContinue)
            {
                args.HttpClient.SetConnection(connection);
                await args.HttpClient.SendRequest(Enable100ContinueBehaviour, args.IsTransparent,
                    cancellationToken);
            }

            // If 100 continue was the response inform that to the client
            if (Enable100ContinueBehaviour)
            {
                var clientStreamWriter = args.ProxyClient.ClientStreamWriter;

                if (request.Is100Continue)
                {
                    await clientStreamWriter.WriteResponseStatusAsync(args.HttpClient.Response.HttpVersion,
                        (int)HttpStatusCode.Continue, "Continue", cancellationToken);
                    await clientStreamWriter.WriteLineAsync(cancellationToken);
                }
                else if (request.ExpectationFailed)
                {
                    await clientStreamWriter.WriteResponseStatusAsync(args.HttpClient.Response.HttpVersion,
                        (int)HttpStatusCode.ExpectationFailed, "Expectation Failed", cancellationToken);
                    await clientStreamWriter.WriteLineAsync(cancellationToken);
                }
            }

            // If expect continue is not enabled then set the connectio and send request headers
            if (!request.ExpectContinue)
            {
                args.HttpClient.SetConnection(connection);
                await args.HttpClient.SendRequest(Enable100ContinueBehaviour, args.IsTransparent,
                    cancellationToken);
            }

            // check if content-length is > 0
            if (request.ContentLength > 0)
            {
                if (request.IsBodyRead)
                {
                    var writer = args.HttpClient.Connection.StreamWriter;
                    await writer.WriteBodyAsync(body, request.IsChunked, cancellationToken);
                }
                else
                {
                    if (!request.ExpectationFailed)
                    {
                        if (request.HasBody)
                        {
                            HttpWriter writer = args.HttpClient.Connection.StreamWriter;
                            await args.CopyRequestBodyAsync(writer, TransformationMode.None, cancellationToken);
                        }
                    }
                }
            }

            args.TimeLine["Request Sent"] = DateTime.Now;

            // If not expectation failed response was returned by server then parse response
            if (!request.ExpectationFailed)
            {
                await handleHttpSessionResponse(args);
            }

            args.TimeLine["Response Sent"] = DateTime.Now;
        }

        /// <summary>
        ///     Prepare the request headers so that we can avoid encodings not parsable by this proxy
        /// </summary>
        private void prepareRequestHeaders(HeaderCollection requestHeaders)
        {
            string acceptEncoding = requestHeaders.GetHeaderValueOrNull(KnownHeaders.AcceptEncoding);

            if (acceptEncoding != null)
            {
                var supportedAcceptEncoding = new List<string>();

                //only allow proxy supported compressions
                supportedAcceptEncoding.AddRange(acceptEncoding.Split(',')
                    .Select(x => x.Trim())
                    .Where(x => ProxyConstants.ProxySupportedCompressions.Contains(x)));

                //uncompressed is always supported by proxy
                supportedAcceptEncoding.Add("identity");

                requestHeaders.SetOrAddHeaderValue(KnownHeaders.AcceptEncoding,
                    string.Join(",", supportedAcceptEncoding));
            }

            requestHeaders.FixProxyHeaders();
        }

        /// <summary>
        ///     Invoke before request handler if it is set.
        /// </summary>
        /// <param name="args">The session event arguments.</param>
        /// <returns></returns>
        private async Task invokeBeforeRequest(SessionEventArgs args)
        {
            if (BeforeRequest != null)
            {
                await BeforeRequest.InvokeAsync(this, args, ExceptionFunc);
            }
        }
    }
}
