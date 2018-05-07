using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
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
    public partial class ProxyServer
    {
        private static readonly Regex uriSchemeRegex =
            new Regex("^[a-z]*://", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> proxySupportedCompressions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gzip",
            "deflate"
        };

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
        private async Task HandleHttpSessionRequest(ProxyEndPoint endPoint, TcpClientConnection clientConnection,
            CustomBufferedStream clientStream, HttpResponseWriter clientStreamWriter,
            CancellationTokenSource cancellationTokenSource, string httpsConnectHostname, ConnectRequest connectRequest)
        {
            var cancellationToken = cancellationTokenSource.Token;
            TcpServerConnection serverConnection = null;

            try
            {
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

                    var args = new SessionEventArgs(BufferSize, endPoint, cancellationTokenSource, ExceptionFunc)
                    {
                        ProxyClient = { ClientConnection = clientConnection },
                        WebSession = { ConnectRequest = connectRequest }
                    };

                    try
                    {
                        try
                        {
                            Request.ParseRequestLine(httpCmd, out string httpMethod, out string httpUrl,
                                out var version);

                            // Read the request headers in to unique and non-unique header collections
                            await HeaderParser.ReadHeaders(clientStream, args.WebSession.Request.Headers,
                                cancellationToken);

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
                            args.ProxyClient.ClientStreamWriter = clientStreamWriter;

                            if (!args.IsTransparent)
                            {
                                // proxy authorization check
                                if (httpsConnectHostname == null && await CheckAuthorization(args) == false)
                                {
                                    await InvokeBeforeResponse(args);

                                    // send the response
                                    await clientStreamWriter.WriteResponseAsync(args.WebSession.Response,
                                        cancellationToken: cancellationToken);
                                    return;
                                }

                                PrepareRequestHeaders(request.Headers);
                                request.Host = request.RequestUri.Authority;
                            }

                            // if win auth is enabled
                            // we need a cache of request body
                            // so that we can send it after authentication in WinAuthHandler.cs
                            if (isWindowsAuthenticationEnabledAndSupported && request.HasBody)
                            {
                                await args.GetRequestBody(cancellationToken);
                            }

                            request.OriginalHasBody = request.HasBody;

                            // If user requested interception do it
                            await InvokeBeforeRequest(args);

                            var response = args.WebSession.Response;

                            if (request.CancelRequest)
                            {
                                // syphon out the request body from client before setting the new body
                                await args.SyphonOutBodyAsync(true, cancellationToken);

                                await HandleHttpSessionResponse(args);

                                if (!response.KeepAlive)
                                {
                                    return;
                                }

                                continue;
                            }

                            // create a new connection if hostname/upstream end point changes
                            if (serverConnection != null
                                && (!serverConnection.HostName.EqualsIgnoreCase(request.RequestUri.Host)
                                    || args.WebSession.UpStreamEndPoint?.Equals(serverConnection.UpStreamEndPoint) ==
                                    false))
                            {
                                serverConnection.Dispose();
                                serverConnection = null;
                            }

                            if (serverConnection == null)
                            {
                                serverConnection = await GetServerConnection(args, false, clientConnection.NegotiatedApplicationProtocol, cancellationToken);
                            }

                            // if upgrading to websocket then relay the requet without reading the contents
                            if (request.UpgradeToWebSocket)
                            {
                                // prepare the prefix content
                                await serverConnection.StreamWriter.WriteLineAsync(httpCmd, cancellationToken);
                                await serverConnection.StreamWriter.WriteHeadersAsync(request.Headers,
                                    cancellationToken: cancellationToken);
                                string httpStatus = await serverConnection.Stream.ReadLineAsync(cancellationToken);

                                Response.ParseResponseLine(httpStatus, out var responseVersion,
                                    out int responseStatusCode,
                                    out string responseStatusDescription);
                                response.HttpVersion = responseVersion;
                                response.StatusCode = responseStatusCode;
                                response.StatusDescription = responseStatusDescription;

                                await HeaderParser.ReadHeaders(serverConnection.Stream, response.Headers,
                                    cancellationToken);

                                if (!args.IsTransparent)
                                {
                                    await clientStreamWriter.WriteResponseAsync(response,
                                        cancellationToken: cancellationToken);
                                }

                                // If user requested call back then do it
                                if (!args.WebSession.Response.Locked)
                                {
                                    await InvokeBeforeResponse(args);
                                }

                                await TcpHelper.SendRaw(clientStream, serverConnection.Stream, BufferSize,
                                    (buffer, offset, count) => { args.OnDataSent(buffer, offset, count); },
                                    (buffer, offset, count) => { args.OnDataReceived(buffer, offset, count); },
                                    cancellationTokenSource, ExceptionFunc);

                                return;
                            }

                            // construct the web request that we are going to issue on behalf of the client.
                            await HandleHttpSessionRequestInternal(serverConnection, args);

                            if (args.WebSession.ServerConnection == null)
                            {
                                return;
                            }

                            // if connection is closing exit
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
                serverConnection?.Dispose();
            }
        }

        /// <summary>
        ///     Handle a specific session (request/response sequence)
        /// </summary>
        /// <param name="serverConnection">The tcp connection.</param>
        /// <param name="args">The session event arguments.</param>
        /// <returns></returns>
        private async Task HandleHttpSessionRequestInternal(TcpServerConnection serverConnection, SessionEventArgs args)
        {
            try
            {
                var cancellationToken = args.CancellationTokenSource.Token;
                var request = args.WebSession.Request;
                request.Locked = true;

                var body = request.CompressBodyAndUpdateContentLength();

                // if expect continue is enabled then send the headers first 
                // and see if server would return 100 conitinue
                if (request.ExpectContinue)
                {
                    args.WebSession.SetConnection(serverConnection);
                    await args.WebSession.SendRequest(Enable100ContinueBehaviour, args.IsTransparent,
                        cancellationToken);
                }

                // If 100 continue was the response inform that to the client
                if (Enable100ContinueBehaviour)
                {
                    var clientStreamWriter = args.ProxyClient.ClientStreamWriter;

                    if (request.Is100Continue)
                    {
                        await clientStreamWriter.WriteResponseStatusAsync(args.WebSession.Response.HttpVersion,
                            (int)HttpStatusCode.Continue, "Continue", cancellationToken);
                        await clientStreamWriter.WriteLineAsync(cancellationToken);
                    }
                    else if (request.ExpectationFailed)
                    {
                        await clientStreamWriter.WriteResponseStatusAsync(args.WebSession.Response.HttpVersion,
                            (int)HttpStatusCode.ExpectationFailed, "Expectation Failed", cancellationToken);
                        await clientStreamWriter.WriteLineAsync(cancellationToken);
                    }
                }

                // If expect continue is not enabled then set the connectio and send request headers
                if (!request.ExpectContinue)
                {
                    args.WebSession.SetConnection(serverConnection);
                    await args.WebSession.SendRequest(Enable100ContinueBehaviour, args.IsTransparent,
                        cancellationToken);
                }

                // check if content-length is > 0
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

                // If not expectation failed response was returned by server then parse response
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
        ///     Create a server connection.
        /// </summary>
        /// <param name="args">The session event arguments.</param>
        /// <param name="isConnect">Is this a CONNECT request.</param>
        /// <param name="applicationProtocol"></param>
        /// <param name="cancellationToken">The cancellation token for this async task.</param>
        /// <returns></returns>
        private Task<TcpServerConnection> GetServerConnection(SessionEventArgsBase args, bool isConnect,
            SslApplicationProtocol applicationProtocol, CancellationToken cancellationToken)
        {
            List<SslApplicationProtocol> applicationProtocols = null;
            if (applicationProtocol != default)
            {
                applicationProtocols = new List<SslApplicationProtocol> { applicationProtocol };
            }

            return GetServerConnection(args, isConnect, applicationProtocols, cancellationToken);
        }

        /// <summary>
        ///     Create a server connection.
        /// </summary>
        /// <param name="args">The session event arguments.</param>
        /// <param name="isConnect">Is this a CONNECT request.</param>
        /// <param name="applicationProtocols"></param>
        /// <param name="cancellationToken">The cancellation token for this async task.</param>
        /// <returns></returns>
        private async Task<TcpServerConnection> GetServerConnection(SessionEventArgsBase args, bool isConnect,
        List<SslApplicationProtocol> applicationProtocols, CancellationToken cancellationToken)
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
                args.WebSession.Request.HttpVersion, 
                isHttps, applicationProtocols, isConnect,
                this, args.WebSession.UpStreamEndPoint ?? UpStreamEndPoint,
                customUpStreamProxy ?? (isHttps ? UpStreamHttpsProxy : UpStreamHttpProxy),
                cancellationToken);
        }

        /// <summary>
        ///     Prepare the request headers so that we can avoid encodings not parsable by this proxy
        /// </summary>
        private void PrepareRequestHeaders(HeaderCollection requestHeaders)
        {
            var acceptEncoding = requestHeaders.GetHeaderValueOrNull(KnownHeaders.AcceptEncoding);

            if (acceptEncoding != null)
            {
                var supportedAcceptEncoding = new List<string>();

                //only allow proxy supported compressions
                supportedAcceptEncoding.AddRange(acceptEncoding.Split(',')
                    .Select(x => x.Trim())
                    .Where(x => proxySupportedCompressions.Contains(x)));

                //uncompressed is always supported by proxy
                supportedAcceptEncoding.Add("identity");

                requestHeaders.SetOrAddHeaderValue(KnownHeaders.AcceptEncoding, string.Join(",", supportedAcceptEncoding));
            }

            requestHeaders.FixProxyHeaders();
        }

        /// <summary>
        ///     Invoke before request handler if it is set.
        /// </summary>
        /// <param name="args">The session event arguments.</param>
        /// <returns></returns>
        private async Task InvokeBeforeRequest(SessionEventArgs args)
        {
            if (BeforeRequest != null)
            {
                await BeforeRequest.InvokeAsync(this, args, ExceptionFunc);
            }
        }
    }
}
