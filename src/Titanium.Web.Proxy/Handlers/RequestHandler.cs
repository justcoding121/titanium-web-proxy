using System;
using System.Collections.Generic;
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

                        // Fill in a default Host header BEFORE BeforeRequest fires so that handlers
                        // can read it and optionally override it.  The value is derived from the raw
                        // RequestUriString8 bytes (not from System.Uri, which may normalise the host)
                        // to stay consistent with the #931 raw-target-preservation fix.
                        if (!args.IsTransparent && !args.IsSocks && request.Host == null)
                        {
                            var rawAuthority = UriExtensions.GetRawAuthority(request.RequestUriString8)
                                               ?? (request.Authority.Length > 0
                                                   ? request.Authority.GetString()
                                                   : null);
                            if (rawAuthority != null)
                                request.Host = rawAuthority;
                        }

                        // If user requested interception do it
                        await OnBeforeRequest(args);

                        // Total per-request deadline starts after BeforeRequest so session overrides apply.
                        using var requestTimeoutScope = ProxyTimeoutScope.Create(cancellationToken,
                            ResolveRequestTimeout(args), ProxyTimeoutKind.Request);
                        var requestToken = requestTimeoutScope.Token;
                        args.OperationCancellationToken = requestToken;

                        try
                        {
                            if (!args.IsTransparent && !args.IsSocks)
                            {
                                // proxy authorization check
                                if (connectRequest == null && await CheckAuthorization(args) == false)
                                {
                                    await OnBeforeResponse(args);

                                    // send the response
                                    await clientStream.WriteResponseAsync(args.HttpClient.Response, requestToken);
                                    args.IsClientResponseCommitted = true;
                                    return;
                                }

                                PrepareRequestHeaders(request.Headers);
                                // Do NOT overwrite Host here — any value set by the BeforeRequest handler
                                // must be preserved.  The default was already filled in above.

                                // Via loop detection and injection (RFC 9110 §7.6.3).
                                if (!string.IsNullOrEmpty(ViaHeaderPseudonym))
                                {
                                    if (HasLoopedVia(request.Headers, ViaHeaderPseudonym))
                                    {
                                        args.HttpClient.Response = new Response
                                        {
                                            HttpVersion = request.HttpVersion,
                                            StatusCode = 508,
                                            StatusDescription = "Loop Detected"
                                        };
                                        // Drain any request body first so the client stream is clean.
                                        if (!(Enable100ContinueBehaviour && request.ExpectContinue))
                                            await args.SyphonOutBodyAsync(true, requestToken);
                                        await clientStream.WriteResponseAsync(args.HttpClient.Response, requestToken);
                                        args.IsClientResponseCommitted = true;
                                        return;
                                    }

                                    AddViaHeader(request.Headers, request.HttpVersion, ViaHeaderPseudonym);
                                }
                            }

                            // if win auth is enabled
                            // we need a cache of request body
                            // so that we can send it after authentication in WinAuthHandler.cs
                            if (args.EnableWinAuth && request.HasBody) await args.GetRequestBody(requestToken);

                            var response = args.HttpClient.Response;

                            if (request.CancelRequest)
                            {
                                if (!(Enable100ContinueBehaviour && request.ExpectContinue))
                                    // syphon out the request body from client before setting the new body
                                    await args.SyphonOutBodyAsync(true, requestToken);

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
                                requestToken, cancellationTokenSource);

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
                                throw new OperationCanceledException("Session was terminated by user.",
                                    cancellationTokenSource.Token);

                            // Release the server connection back to the shared pool after each HTTP session
                            // (rather than holding it for the whole client connection). This is more efficient
                            // when a client idly holds a server connection between sessions without using it.
                            // We only get here when the response was persistent (response.KeepAlive above) and its
                            // body was fully received, so the connection is at a clean message boundary and safe to reuse.
                            // WinAuth (NTLM/Negotiate) connections are deliberately NOT returned to the shared pool:
                            // they are authenticated to a specific identity and are connection-oriented, so they stay
                            // bound to this client session (reused for its subsequent requests) and are closed when
                            // the client connection ends, never shared with another client.
                            if (EnableConnectionPool && connection != null
                                                     && !connection.IsWinAuthenticated)
                            {
                                await TcpConnectionFactory.Release(connection);
                                connection = null;
                            }
                        }
                        catch (ProxyTimeoutException timeoutEx)
                        {
                            await HandleProxyTimeoutAsync(args, timeoutEx, cancellationToken);
                            closeServerConnection = true;
                            return;
                        }
                        catch (Exception ex) when (ex is OperationCanceledException || requestTimeoutScope.IsTimedOut())
                        {
                            if (requestTimeoutScope.IsTimedOut())
                            {
                                var timeoutEx = new ProxyTimeoutException(
                                    "Proxy request timeout elapsed.", ProxyTimeoutKind.Request, ex);
                                await HandleProxyTimeoutAsync(args, timeoutEx, cancellationToken);
                                closeServerConnection = true;
                                return;
                            }

                            throw;
                        }
                    }
                    // Do not wrap cancellation or retryable connection failures: they are expected
                    // control-flow outcomes (or already typed) and wrapping them as ProxyHttpException
                    // only adds allocations and can elevate severity in diagnostics.
                    catch (Exception e) when (!(e is ProxyHttpException)
                                              && !(e is ProxyTimeoutException)
                                              && !(e is OperationCanceledException)
                                              && !(e is RetryableServerConnectionException))
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

        // H1.1 client → H3 origin bridge: resolve route (including SVCB DNS on cold start).
        if (!args.HttpClient.Request.UpgradeToWebSocket)
        {
            var reqHost = args.HttpClient.Request.RequestUri?.Host ?? string.Empty;
            var reqPort = args.HttpClient.Request.RequestUri?.Port ?? 443;
            var h3Route = await ResolveHttp3OriginAsync(
                reqHost, reqPort,
                args.UpstreamHttpProtocol,
                allowDnsProbe: true,
                cancellationToken);

            if (h3Route.UseH3)
            {
                await Http3.Http3OriginBridge.ForwardAsync(args, this, h3Route, logger, cancellationToken);
                return new RetryResult(null, null, true);
            }
        }

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
        return await RetryPolicy<RetryableServerConnectionException>(args).ExecuteAsync(async connection =>
        {
            // set the connection and send request headers
            args.HttpClient.SetConnection(connection);

            if (args.Timing != null)
                args.Timing.MarkConnectionReady(connection.Id, !connection.ClaimFirstUse());

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
        var cancellationToken = args.CancellationToken;
        var request = args.HttpClient.Request;

        var body = request.CompressBodyAndUpdateContentLength();

        await args.HttpClient.SendRequest(Enable100ContinueBehaviour, args.IsTransparent,
            args.OriginHttpVersionPolicy ?? OriginHttpVersionPolicy, cancellationToken);

        // If a successful 100 continue request was made, inform that to the client and reset response
        if (request.ExpectationSucceeded)
        {
            var writer = args.ClientStream;
            var response = args.HttpClient.Response;

            var headerBuilder = new HeaderBuilder();
            headerBuilder.WriteResponseLine(response.HttpVersion, response.StatusCode, response.StatusDescription);
            headerBuilder.WriteHeaders(response.Headers);
            await writer.WriteHeadersAsync(headerBuilder, cancellationToken);
            args.IsClientResponseCommitted = true;

            await args.ClearResponse(cancellationToken);
        }

        // send body to server if available (idle-write window on stalled transfers)
        if (request.HasBody)
        {
            // In compatibility mode, send a synthetic 100 Continue to the client before reading
            // the body so that a strict Expect: 100-continue client does not deadlock waiting
            // for a 100 that the proxy would never send (because Enable100ContinueBehaviour=false).
            if (CompatibilityMode100Continue && !Enable100ContinueBehaviour && request.ExpectContinue
                && !request.IsBodyRead && !request.ExpectationFailed)
            {
                var continueBuilder = new HeaderBuilder();
                continueBuilder.WriteResponseLine(request.HttpVersion, 100, "Continue");
                // WriteHeaders writes the empty terminator line; with no actual headers, this emits
                // just the final \r\n that completes a valid 100 Continue response.
                continueBuilder.WriteHeaders(new HeaderCollection());
                await args.ClientStream.WriteHeadersAsync(continueBuilder, cancellationToken);
            }

            using var idleWriteScope = ProxyTimeoutScope.Create(cancellationToken,
                ResolveIdleWriteTimeout(args), ProxyTimeoutKind.IdleWrite);
            try
            {
                if (request.IsBodyRead)
                    await args.HttpClient.Connection.Stream.WriteBodyAsync(body!, request.IsChunked,
                        request.HasTrailingHeaders ? request.TrailingHeaders : null, idleWriteScope.Token);
                else if (!request.ExpectationFailed)
                    // get the request body unless an unsuccessful 100 continue request was made
                    await args.CopyRequestBodyAsync(args.HttpClient.Connection.Stream, TransformationMode.None,
                        idleWriteScope.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException || idleWriteScope.IsTimedOut())
            {
                idleWriteScope.ThrowIfTimedOut(ex);
                throw;
            }
        }

        args.Timing?.MarkRequestSent();

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
            var remaining = acceptEncoding.AsSpan();
            while (remaining.Length > 0)
            {
                int comma = remaining.IndexOf(',');
                var token = (comma < 0 ? remaining : remaining.Slice(0, comma)).Trim();
                if (token.Length > 0)
                {
                    var s = token.ToString();
                    if (ProxyConstants.ProxySupportedCompressions.Contains(s))
                        supportedAcceptEncoding.Add(s);
                }

                if (comma < 0) break;
                remaining = remaining.Slice(comma + 1);
            }

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
        args.Timing?.MarkRequestHeadersReceived();

        if (BeforeRequest != null) await BeforeRequest.InvokeAsync(this, args, logger);
    }

    /// <summary>
    ///     Invoke before request handler if it is set.
    /// </summary>
    /// <param name="request">The COONECT request.</param>
    /// <returns></returns>
    internal async Task OnBeforeUpStreamConnectRequest(ConnectRequest request)
    {
        if (BeforeUpStreamConnectRequest != null)
            await BeforeUpStreamConnectRequest.InvokeAsync(this, request, logger);
    }

    internal bool ShouldCallBeforeRequestBodyWrite()
    {
        return OnRequestBodyWrite != null;
    }

    internal async Task OnBeforeRequestBodyWrite(BeforeBodyWriteEventArgs args)
    {
        if (OnRequestBodyWrite != null)
        {
            await OnRequestBodyWrite.InvokeAsync(this, args, logger);
        }
    }

    /// <summary>
    ///     Appends a Via header entry to <paramref name="headers" /> per RFC 9110 §7.6.3.
    ///     If a Via header already exists its value is extended with a comma-separated suffix.
    /// </summary>
    internal static void AddViaHeader(HeaderCollection headers, Version httpVersion, string pseudonym)
    {
        // Via uses HTTP's protocol-version token, whose canonical form includes both digits
        // (RFC 9110 §2.5/§7.6.3): 1.1, 2.0, 3.0. Sending "2" is accepted by many origins,
        // but strict servers such as play.google.com reject it with RST_STREAM(PROTOCOL_ERROR).
        var protocol = $"{httpVersion.Major}.{httpVersion.Minor}";
        var entry = $"{protocol} {pseudonym}";

        var existing = headers.GetHeaders("Via");
        if (existing is { Count: > 0 })
        {
            // Keep this operation idempotent only for the exact received-protocol
            // entry. The same pseudonym with a different protocol represents a
            // distinct hop and must not suppress the correct entry.
            bool alreadyPresent = false;
            foreach (var header in existing)
            {
                var remaining = header.Value.AsSpan();
                while (remaining.Length > 0)
                {
                    int comma = remaining.IndexOf(',');
                    var token = (comma < 0 ? remaining : remaining.Slice(0, comma)).Trim();
                    if (token.Length > 0 && ViaEntryMatches(token.ToString(), protocol, pseudonym))
                    {
                        alreadyPresent = true;
                        break;
                    }

                    if (comma < 0) break;
                    remaining = remaining.Slice(comma + 1);
                }

                if (alreadyPresent) break;
            }

            if (!alreadyPresent)
                existing[0].SetValue($"{existing[0].Value}, {entry}");

            // Re-create all Via fields with a lowercase name. Lowercase is harmless
            // for HTTP/1.x and mandatory when this collection is HPACK-encoded.
            headers.RemoveHeader("Via");
            foreach (var header in existing)
                headers.AddHeader("via", header.Value);
        }
        else
        {
            // Lowercase is valid for HTTP/1.x and required when this collection is encoded as HTTP/2.
            headers.AddHeader("via", entry);
        }
    }

    private static readonly char[] ViaWhitespaceChars = { ' ', '\t' };

    private static bool ViaEntryMatches(string viaEntry, string protocol, string pseudonym)
    {
        int separator = viaEntry.IndexOfAny(ViaWhitespaceChars);
        if (separator <= 0 ||
            !string.Equals(viaEntry.Substring(0, separator), protocol, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ViaTokenMatches(viaEntry, pseudonym);
    }

    /// <summary>
    ///     Returns true if the received-by host in a single Via list entry matches
    ///     <paramref name="pseudonym" /> exactly (case-insensitive), ignoring any optional port suffix.
    ///     Prevents false positives from suffix substring matches (e.g. "proxy" matching "my-proxy").
    /// </summary>
    private static bool ViaTokenMatches(string viaEntry, string pseudonym)
    {
        // A Via entry is: received-protocol RWS received-by [ RWS comment ].
        // RFC 9110 RWS permits SP or HTAB, and received-by can include an optional port.
        int separator = viaEntry.IndexOfAny(ViaWhitespaceChars);
        if (separator < 0) return false;

        int receivedByStart = separator;
        while (receivedByStart < viaEntry.Length &&
               (viaEntry[receivedByStart] == ' ' || viaEntry[receivedByStart] == '\t'))
        {
            receivedByStart++;
        }

        int receivedByEnd = receivedByStart;
        while (receivedByEnd < viaEntry.Length &&
               viaEntry[receivedByEnd] != ' ' && viaEntry[receivedByEnd] != '\t')
        {
            receivedByEnd++;
        }

        if (receivedByEnd == receivedByStart) return false;

        var receivedBy = viaEntry.Substring(receivedByStart, receivedByEnd - receivedByStart);
        if (string.Equals(receivedBy, pseudonym, StringComparison.OrdinalIgnoreCase)) return true;

        // Normalize bracketed IPv6 received-by values before comparing with a bare
        // IPv6 pseudonym. Any suffix after ']' must be an optional numeric port.
        if (receivedBy.Length > 2 && receivedBy[0] == '[')
        {
            int closingBracket = receivedBy.IndexOf(']');
            if (closingBracket > 1)
            {
                var host = receivedBy.Substring(1, closingBracket - 1);
                var suffix = receivedBy.Substring(closingBracket + 1);
                if (string.Equals(host, pseudonym, StringComparison.OrdinalIgnoreCase) &&
                    (suffix.Length == 0 || IsNumericPortSuffix(suffix)))
                {
                    return true;
                }
            }
        }

        // A configured pseudonym denotes the received-by host/token. Match an optional
        // numeric port, but never suffixes such as "my-proxy" or "proxy.example".
        if (receivedBy.Length <= pseudonym.Length ||
            !receivedBy.StartsWith(pseudonym, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsNumericPortSuffix(receivedBy.Substring(pseudonym.Length));
    }

    private static bool IsNumericPortSuffix(string suffix)
    {
        if (suffix.Length < 2 || suffix[0] != ':') return false;

        for (int i = 1; i < suffix.Length; i++)
        {
            if (suffix[i] < '0' || suffix[i] > '9') return false;
        }

        return true;
    }

    /// <summary>
    ///     Returns true if any token in any Via header field already names <paramref name="pseudonym" />,
    ///     indicating a proxy loop. Checks all Via header fields (RFC 9110 allows multiple field lines).
    /// </summary>
    internal static bool HasLoopedVia(HeaderCollection headers, string pseudonym)
    {
        var viaHeaders = headers.GetHeaders("Via");
        if (viaHeaders == null || viaHeaders.Count == 0) return false;

        foreach (var header in viaHeaders)
        {
            var remaining = header.Value.AsSpan();
            while (remaining.Length > 0)
            {
                int comma = remaining.IndexOf(',');
                var token = (comma < 0 ? remaining : remaining.Slice(0, comma)).Trim();
                if (token.Length > 0 && ViaTokenMatches(token.ToString(), pseudonym)) return true;
                if (comma < 0) break;
                remaining = remaining.Slice(comma + 1);
            }
        }

        return false;
    }
}