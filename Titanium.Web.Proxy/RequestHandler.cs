using System;
using System.Net;
using System.Net.Sockets;
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
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy
{
    /// <summary>
    ///     Handle the request
    /// </summary>
    partial class ProxyServer
    {
        private static readonly Regex uriSchemeRegex =
            new Regex("^[a-z]*://", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private bool isWindowsAuthenticationEnabledAndSupported =>
            EnableWinAuth && RunTime.IsWindows && !RunTime.IsRunningOnMono;

        /// <summary>
        ///     This is the core request handler method for a particular connection from client
        ///     Will create new session (request/response) sequence until
        ///     client/server abruptly terminates connection or by normal HTTP termination
        /// </summary>
        /// <param name="client"></param>
        /// <param name="clientStream"></param>
        /// <param name="clientStreamReader"></param>
        /// <param name="clientStreamWriter"></param>
        /// <param name="cancellationTokenSource"></param>
        /// <param name="httpsConnectHostname"></param>
        /// <param name="endPoint"></param>
        /// <param name="connectRequest"></param>
        /// <param name="isTransparentEndPoint"></param>
        /// <returns></returns>
        private async Task HandleHttpSessionRequest(ProxyEndPoint endPoint, TcpClient client,
            CustomBufferedStream clientStream, CustomBinaryReader clientStreamReader,
            HttpResponseWriter clientStreamWriter,
            CancellationTokenSource cancellationTokenSource, string httpsConnectHostname, ConnectRequest connectRequest,
            bool isTransparentEndPoint = false)
        {
            TcpConnection connection = null;

            try
            {
                //Loop through each subsequest request on this particular client connection
                //(assuming HTTP connection is kept alive by client)
                while (true)
                {
                    // read the request line
                    string httpCmd = await clientStreamReader.ReadLineAsync(cancellationTokenSource.Token);
                    if (httpCmd == "PRI * HTTP/2.0")
                    {
                        // HTTP/2 Connection Preface
                        string line = await clientStreamReader.ReadLineAsync(cancellationTokenSource.Token);
                        if (line != string.Empty) throw new Exception($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");

                        line = await clientStreamReader.ReadLineAsync(cancellationTokenSource.Token);
                        if (line != "SM") throw new Exception($"HTTP/2 Protocol violation. 'SM' expected, '{line}' received");

                        line = await clientStreamReader.ReadLineAsync(cancellationTokenSource.Token);
                        if (line != string.Empty) throw new Exception($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");

                        // todo
                        var buffer = new byte[1024];
                        await clientStreamReader.ReadBytesAsync(buffer, 0, 3, cancellationTokenSource.Token);
                    }

                    if (string.IsNullOrEmpty(httpCmd))
                    {
                        return;
                    }

                    var args = new SessionEventArgs(BufferSize, endPoint, cancellationTokenSource, ExceptionFunc)
                    {
                        ProxyClient = { TcpClient = client },
                        WebSession = { ConnectRequest = connectRequest }
                    };

                    try
                    {
                        try
                        {
                            Request.ParseRequestLine(httpCmd, out string httpMethod, out string httpUrl,
                                out var version);

                            //Read the request headers in to unique and non-unique header collections
                            await HeaderParser.ReadHeaders(clientStreamReader, args.WebSession.Request.Headers, cancellationTokenSource.Token);

                            Uri httpRemoteUri;
                            if (uriSchemeRegex.IsMatch(httpUrl))
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
                                string host = args.WebSession.Request.Host ?? httpsConnectHostname;
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

                            var request = args.WebSession.Request;
                            request.RequestUri = httpRemoteUri;
                            request.OriginalUrl = httpUrl;

                            request.Method = httpMethod;
                            request.HttpVersion = version;
                            args.ProxyClient.ClientStream = clientStream;
                            args.ProxyClient.ClientStreamReader = clientStreamReader;
                            args.ProxyClient.ClientStreamWriter = clientStreamWriter;

                            //proxy authorization check
                            if (!args.IsTransparent && httpsConnectHostname == null &&
                                await CheckAuthorization(args) == false)
                            {
                                await InvokeBeforeResponse(args);

                                //send the response
                                await clientStreamWriter.WriteResponseAsync(args.WebSession.Response);
                                return;
                            }

                            if (!isTransparentEndPoint)
                            {
                                PrepareRequestHeaders(request.Headers);
                                request.Host = request.RequestUri.Authority;
                            }

                            //if win auth is enabled
                            //we need a cache of request body
                            //so that we can send it after authentication in WinAuthHandler.cs
                            if (isWindowsAuthenticationEnabledAndSupported && request.HasBody)
                            {
                                await args.GetRequestBody();
                            }

                            request.OriginalHasBody = request.HasBody;

                            //If user requested interception do it
                            await InvokeBeforeRequest(args);

                            var response = args.WebSession.Response;

                            if (request.CancelRequest)
                            {
                                //syphon out the request body from client before setting the new body
                                await args.SyphonOutBodyAsync(true, cancellationTokenSource.Token);

                                await HandleHttpSessionResponse(args);

                                if (!response.KeepAlive)
                                {
                                    return;
                                }

                                continue;
                            }

                            //create a new connection if hostname/upstream end point changes
                            if (connection != null
                                && (!connection.HostName.Equals(request.RequestUri.Host,
                                        StringComparison.OrdinalIgnoreCase)
                                    || args.WebSession.UpStreamEndPoint != null
                                    && !args.WebSession.UpStreamEndPoint.Equals(connection.UpStreamEndPoint)))
                            {
                                connection.Dispose();
                                connection = null;
                            }

                            if (connection == null)
                            {
                                connection = await GetServerConnection(args, false, cancellationTokenSource.Token);
                            }

                            //if upgrading to websocket then relay the requet without reading the contents
                            if (request.UpgradeToWebSocket)
                            {
                                //prepare the prefix content
                                await connection.StreamWriter.WriteLineAsync(httpCmd);
                                await connection.StreamWriter.WriteHeadersAsync(request.Headers);
                                string httpStatus = await connection.StreamReader.ReadLineAsync(cancellationTokenSource.Token);

                                Response.ParseResponseLine(httpStatus, out var responseVersion,
                                    out int responseStatusCode,
                                    out string responseStatusDescription);
                                response.HttpVersion = responseVersion;
                                response.StatusCode = responseStatusCode;
                                response.StatusDescription = responseStatusDescription;

                                await HeaderParser.ReadHeaders(connection.StreamReader, response.Headers, cancellationTokenSource.Token);

                                if (!args.IsTransparent)
                                {
                                    await clientStreamWriter.WriteResponseAsync(response);
                                }

                                //If user requested call back then do it
                                if (!args.WebSession.Response.Locked)
                                {
                                    await InvokeBeforeResponse(args);
                                }

                                await TcpHelper.SendRaw(clientStream, connection.Stream, BufferSize,
                                    (buffer, offset, count) => { args.OnDataSent(buffer, offset, count); },
                                    (buffer, offset, count) => { args.OnDataReceived(buffer, offset, count); },
                                    cancellationTokenSource, ExceptionFunc);

                                return;
                            }

                            //construct the web request that we are going to issue on behalf of the client.
                            await HandleHttpSessionRequestInternal(connection, args);

                            if (args.WebSession.ServerConnection == null)
                            {
                                return;
                            }

                            //if connection is closing exit
                            if (!response.KeepAlive)
                            {
                                return;
                            }

                            if (cancellationTokenSource.IsCancellationRequested)
                            {
                                throw new Exception("Session was terminated by user.");
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
                        throw;
                    }
                    finally
                    {
                        await InvokeAfterResponse(args);
                        args.Dispose();
                    }
                }
            }
            finally
            {
                connection?.Dispose();
            }
        }

        /// <summary>
        ///     Handle a specific session (request/response sequence)
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="args"></param>
        /// <returns>True if close the connection</returns>
        private async Task HandleHttpSessionRequestInternal(TcpConnection connection, SessionEventArgs args)
        {
            try
            {
                var cancellationToken = args.CancellationTokenSource.Token;
                var request = args.WebSession.Request;
                request.Locked = true;

                var body = request.CompressBodyAndUpdateContentLength();

                //if expect continue is enabled then send the headers first 
                //and see if server would return 100 conitinue
                if (request.ExpectContinue)
                {
                    args.WebSession.SetConnection(connection);
                    await args.WebSession.SendRequest(Enable100ContinueBehaviour, args.IsTransparent, cancellationToken);
                }

                //If 100 continue was the response inform that to the client
                if (Enable100ContinueBehaviour)
                {
                    var clientStreamWriter = args.ProxyClient.ClientStreamWriter;

                    if (request.Is100Continue)
                    {
                        await clientStreamWriter.WriteResponseStatusAsync(args.WebSession.Response.HttpVersion,
                            (int)HttpStatusCode.Continue, "Continue");
                        await clientStreamWriter.WriteLineAsync(cancellationToken);
                    }
                    else if (request.ExpectationFailed)
                    {
                        await clientStreamWriter.WriteResponseStatusAsync(args.WebSession.Response.HttpVersion,
                            (int)HttpStatusCode.ExpectationFailed, "Expectation Failed");
                        await clientStreamWriter.WriteLineAsync(cancellationToken);
                    }
                }

                //If expect continue is not enabled then set the connectio and send request headers
                if (!request.ExpectContinue)
                {
                    args.WebSession.SetConnection(connection);
                    await args.WebSession.SendRequest(Enable100ContinueBehaviour, args.IsTransparent, cancellationToken);
                }

                //check if content-length is > 0
                if (request.ContentLength > 0)
                {
                    if (request.IsBodyRead)
                    {
                        var writer = args.WebSession.ServerConnection.StreamWriter;
                        await writer.WriteBodyAsync(body, request.IsChunked, cancellationToken);
                    }
                    else
                    {
                        if (!request.ExpectationFailed)
                        {
                            if (request.HasBody)
                            {
                                HttpWriter writer = args.WebSession.ServerConnection.StreamWriter;
                                await args.CopyRequestBodyAsync(writer, TransformationMode.None, cancellationToken);
                            }
                        }
                    }
                }

                //If not expectation failed response was returned by server then parse response
                if (!request.ExpectationFailed)
                {
                    await HandleHttpSessionResponse(args);
                }
            }
            catch (Exception e) when (!(e is ProxyHttpException))
            {
                throw new ProxyHttpException("Error occured whilst handling session request (internal)", e, args);
            }
        }

        /// <summary>
        ///     Create a Server Connection
        /// </summary>
        /// <param name="args"></param>
        /// <param name="isConnect"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<TcpConnection> GetServerConnection(SessionEventArgsBase args, bool isConnect, CancellationToken cancellationToken)
        {
            ExternalProxy customUpStreamProxy = null;

            bool isHttps = args.IsHttps;
            if (GetCustomUpStreamProxyFunc != null)
            {
                customUpStreamProxy = await GetCustomUpStreamProxyFunc(args);
            }

            args.CustomUpStreamProxyUsed = customUpStreamProxy;

            return await tcpConnectionFactory.CreateClient(
                args.WebSession.Request.RequestUri.Host,
                args.WebSession.Request.RequestUri.Port,
                args.WebSession.ConnectRequest?.ClientHelloInfo?.GetAlpn(),
                args.WebSession.Request.HttpVersion, isHttps, isConnect,
                this, args.WebSession.UpStreamEndPoint ?? UpStreamEndPoint,
                customUpStreamProxy ?? (isHttps ? UpStreamHttpsProxy : UpStreamHttpProxy),
                cancellationToken);
        }

        /// <summary>
        ///     prepare the request headers so that we can avoid encodings not parsable by this proxy
        /// </summary>
        /// <param name="requestHeaders"></param>
        private void PrepareRequestHeaders(HeaderCollection requestHeaders)
        {
            if (requestHeaders.HeaderExists(KnownHeaders.AcceptEncoding))
            {
                requestHeaders.SetOrAddHeaderValue(KnownHeaders.AcceptEncoding, "gzip,deflate");
            }

            requestHeaders.FixProxyHeaders();
        }

        private async Task InvokeBeforeRequest(SessionEventArgs args)
        {
            if (BeforeRequest != null)
            {
                await BeforeRequest.InvokeAsync(this, args, ExceptionFunc);
            }
        }
    }
}
