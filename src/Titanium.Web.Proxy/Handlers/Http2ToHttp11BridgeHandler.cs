#if NET6_0_OR_GREATER
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy;

/// <summary>
///     Translates an h2 client connection onto an HTTP/1.1-only origin (<see cref="UpstreamHttpProtocol.Http11" />
///     with <c>AllowHttpProtocolTranslation</c> enabled - see <see cref="ResolveHttp2ForClientAsync" />), one h2
///     stream at a time.
/// </summary>
/// <remarks>
///     Rather than duplicating <see cref="Http2Helper" />'s frame parsing/HPACK/flow-control machinery, this
///     drives the very same <see cref="Http2Helper.SendHttp2" /> relay used for a real h2-to-h2/h2-to-origin-h2
///     connection, but with a <see cref="NullOriginStream" /> standing in for "the server" - every request is
///     instead answered independently by <see cref="RunHttp2ToHttp11BridgeRoundTripAsync" />, which is what
///     gives every h2 stream its own, independently managed HTTP/1.1 origin connection/round trip rather than
///     coupling them to a single shared upstream connection (which would not be possible anyway: HTTP/1.1
///     connections are not multiplexed).
///     <para>
///         Known simplifications versus the full HTTP/1.1 request/response pipeline (the private
///         <c>HandleHttpSessionRequest</c>/<c>HandleHttpSessionResponse</c> methods a real HTTP/1.1 client
///         session goes through) that a real h2 client happens to exercise here: the request body is fully
///         buffered (via <see cref="SessionEventArgs.GetRequestBody(CancellationToken)" />) before the origin
///         round trip starts rather than streamed live, and Windows Authentication/expect-100-continue
///         relay/origin re-request-on-401-407/interim-response relay are not implemented - WinAuth in particular
///         is connection-oriented and, per RFC 7540 §9.2.3, not meaningful for an h2 client in the first place
///         (see the WinAuth remarks in wiki/Protocol-Support.md).
///     </para>
/// </remarks>
public partial class ProxyServer
{
    /// <summary>
    ///     Entry point for the h2-client-to-HTTP/1.1-origin bridge, invoked once per h2 client connection from
    ///     the explicit and transparent client handlers in place of the normal <see cref="Http2Helper.SendHttp2" />
    ///     call used when both sides speak h2.
    /// </summary>
    /// <param name="clientStream">The (already TLS-authenticated, ALPN="h2") client-facing stream.</param>
    /// <param name="endPoint">The proxy endpoint this connection arrived on.</param>
    /// <param name="connectRequest">The explicit CONNECT request that established this tunnel, if any.</param>
    /// <param name="userData">User data to seed every per-stream <see cref="SessionEventArgs" /> with.</param>
    /// <param name="remoteHostName">The origin identity used for TLS SNI/certificate validation.</param>
    /// <param name="remotePort">The origin identity port, paired with <paramref name="remoteHostName" />.</param>
    /// <param name="connectHost">The actual TCP connect destination override, if a fixed forward target applies.</param>
    /// <param name="connectPort">The actual TCP connect destination override port.</param>
    /// <param name="cancellationTokenSource">Cancellation for the whole client connection.</param>
    internal async Task SendHttp2ToHttp11Bridge(HttpClientStream clientStream, ProxyEndPoint endPoint,
        ConnectRequest? connectRequest, object? userData, string remoteHostName, int remotePort,
        string? connectHost, int? connectPort, CancellationTokenSource cancellationTokenSource)
    {
        var cancellationToken = cancellationTokenSource.Token;
        var originStream = new NullOriginStream(cancellationToken);

        await Http2Helper.SendHttp2(clientStream, originStream,
            () => new SessionEventArgs(this, endPoint, clientStream, connectRequest, cancellationTokenSource)
            {
                UserData = userData
            },
            (sessionArgs, ctx) => BridgeOnBeforeRequest(sessionArgs, ctx, remoteHostName, remotePort, connectHost,
                connectPort),
            // never actually invoked: NullOriginStream never produces a real response HEADERS frame for
            // CopyHttp2FrameAsync's isClient=false direction to decode.
            (sessionArgs, ctx) => Task.CompletedTask,
            async sessionArgs => { await OnAfterResponse(sessionArgs); },
            headers => PrepareRequestHeaders(headers),
            cancellationTokenSource, clientStream.Connection.Id, ExceptionFunc);
    }

    /// <summary>
    ///     The bridge's <c>onBeforeRequest</c> delegate: runs the real user <c>BeforeRequest</c> handlers exactly
    ///     like a normal h2 (or HTTP/1.1) session would, then - unless the request was already answered
    ///     synthetically - buffers the request body and hands the actual origin round trip off to a background
    ///     task (<see cref="RunHttp2ToHttp11BridgeRoundTripAsync" />) tracked the same way a BeforeRequest-time
    ///     synthetic response is (see <see cref="Http2ConnectionState.PendingSynthetics" /> and
    ///     <see cref="Http2StreamState.SyntheticTask" />), so that one stream's origin round trip never blocks
    ///     <see cref="Http2Helper" />'s frame-reading loop - and therefore every other multiplexed stream on this
    ///     same client connection - while it is in flight.
    /// </summary>
    private async Task BridgeOnBeforeRequest(SessionEventArgs sessionArgs, Http2StreamContext ctx,
        string remoteHostName, int remotePort, string? connectHost, int? connectPort)
    {
        await OnBeforeRequest(sessionArgs);

        if (sessionArgs.HttpClient.Request.CancelRequest)
        {
            // answered synthetically (Ok/GenericResponse/Redirect/RespondStreaming) during BeforeRequest -
            // Http2Helper's own ProcessCompleteHeaderBlockAsync dispatches this exactly like it would for a
            // real h2-to-h2/h1.1 relay; there is nothing to bridge.
            return;
        }

        // Buffer the whole request body before starting the HTTP/1.1 origin round trip (see the "known
        // simplifications" remarks on this class). Calling GetRequestBody hands control on this stream's
        // HEADERS block back to Http2Helper.ProcessCompleteHeaderBlockAsync (see its ReadHttp2BeforeHandlerTaskCompletionSource
        // handoff) so the frame-reading loop is never blocked waiting for this method itself to return.
        // GetRequestBody() throws for a request with no body at all (e.g. a bodiless GET) rather than
        // returning an empty array, so it must only be called when the client actually declared a body -
        // there are no DATA frames coming for this stream in that case anyway.
        if (sessionArgs.HttpClient.Request.HasBody) await sessionArgs.GetRequestBody(ctx.CancellationToken);

        if (!ctx.ConnectionState.Streams.TryGetValue(ctx.StreamId, out var streamState))
        {
            // the client already reset this stream (or the whole connection is tearing down) while the body
            // was being read; nothing left to answer.
            return;
        }

        var bridgeTask = RunHttp2ToHttp11BridgeRoundTripAsync(sessionArgs, ctx.StreamId, ctx.ConnectionState,
                ctx.ClientStream, remoteHostName, remotePort, connectHost, connectPort, ctx.CancellationToken,
                streamState.Cancellation.Token)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    ExceptionFunc?.Invoke(new ProxyHttpException(
                        $"HTTP/2-to-HTTP/1.1 bridge round trip failed for stream {ctx.StreamId}",
                        t.Exception!.GetBaseException(), sessionArgs));
            }, TaskScheduler.Default);
        streamState.SyntheticTask = bridgeTask;
        ctx.ConnectionState.PendingSynthetics.Add(bridgeTask);
    }

    /// <summary>
    ///     Performs one independent HTTP/1.1 origin round trip for a single h2 stream and translates the result
    ///     back into h2 frames for the real client, using the same <see cref="Http2Helper.EmitSyntheticResponseAsync" />
    ///     primitive a BeforeRequest-time synthetic response uses. Every h2 stream that reaches this method gets
    ///     its own <see cref="TcpServerConnection" /> (pooled/released independently through
    ///     <see cref="TcpConnectionFactory" /> exactly like an HTTP/1.1 client's requests would), so multiple
    ///     concurrent streams on the same h2 client connection never contend on one shared origin connection.
    /// </summary>
    private async Task RunHttp2ToHttp11BridgeRoundTripAsync(SessionEventArgs sessionArgs, int streamId,
        Http2ConnectionState connectionState, System.IO.Stream clientStream, string remoteHostName, int remotePort,
        string? connectHost, int? connectPort, CancellationToken connectionToken, CancellationToken streamToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, streamToken);
        var cancellationToken = linkedCts.Token;

        var request = sessionArgs.HttpClient.Request;
        TcpServerConnection? connection = null;
        var closeConnection = true;

        try
        {
            // Translate the h2 request onto the wire shape an HTTP/1.1 origin expects: h2 clients send
            // ":authority" (already copied into Request.Authority by Http2Helper) instead of a literal Host
            // header, and the request line HttpWebClient.SendRequest below builds needs an HTTP/1.1 version.
            request.HttpVersion = HttpHeader.Version11;
            if (string.IsNullOrEmpty(request.Host)) request.Host = request.Authority.GetString();

            // RFC 7540 §8.1.2.5: an h2 client may split the Cookie request header across several HEADERS
            // field lines purely for better HPACK compression; the origin still sees the exact same
            // logical value either way over h2. An HTTP/1.1 origin has no such allowance - it expects
            // exactly one "Cookie" header with the individual cookie-pairs joined by "; " - so multiple
            // fields must be re-combined here before this request ever reaches the h1.1 wire.
            var cookieHeaders = request.Headers.GetHeaders("Cookie");
            if (cookieHeaders is { Count: > 1 })
            {
                var combinedCookie = string.Join("; ", cookieHeaders.Select(h => h.Value));
                request.Headers.RemoveHeader("Cookie");
                request.Headers.AddHeader("Cookie", combinedCookie);
            }

            PrepareRequestHeaders(request.Headers);

            var customUpStreamProxy = sessionArgs.CustomUpStreamProxy;
            if (customUpStreamProxy == null && GetCustomUpStreamProxyFunc != null)
                customUpStreamProxy = await GetCustomUpStreamProxyFunc(sessionArgs);
            sessionArgs.CustomUpStreamProxyUsed = customUpStreamProxy;

            var newConnection = await TcpConnectionFactory.GetServerConnection(this, remoteHostName, remotePort,
                HttpHeader.Version11, true, SslExtensions.Http11ProtocolAsList, false, sessionArgs,
                sessionArgs.HttpClient.UpStreamEndPoint ?? UpStreamEndPoint,
                customUpStreamProxy ?? UpStreamHttpsProxy, false, false, cancellationToken, connectHost,
                connectPort)
                ?? throw new Exception($"Failed to establish an HTTP/1.1 origin connection to '{remoteHostName}:{remotePort}'.");
            connection = newConnection;

            sessionArgs.HttpClient.SetConnection(newConnection);
            sessionArgs.TimeLine["Connection Ready"] = DateTime.UtcNow;

            // Matches HandleHttpSessionRequest's HTTP/1.1 send sequence: compute the (possibly re-compressed)
            // body and its Content-Length *before* SendRequest writes the request line/headers, then stream
            // the already-buffered bytes (GetRequestBody above guarantees IsBodyRead is always true here,
            // unlike the HTTP/1.1 path which may still need to copy the body live off the client stream).
            var body = request.CompressBodyAndUpdateContentLength();

            await sessionArgs.HttpClient.SendRequest(Enable100ContinueBehaviour, true, OriginHttpVersionPolicy,
                cancellationToken);

            if (request.HasBody && !request.ExpectationFailed)
                await connection.Stream.WriteBodyAsync(body ?? Array.Empty<byte>(), request.IsChunked,
                    request.HasTrailingHeaders ? request.TrailingHeaders : null, cancellationToken);

            sessionArgs.TimeLine["Request Sent"] = DateTime.UtcNow;

            await sessionArgs.HttpClient.ReceiveResponse(cancellationToken);
            sessionArgs.TimeLine["Response Received"] = DateTime.UtcNow;

            sessionArgs.HttpClient.Response.SetOriginalHeaders();

            if (!sessionArgs.HttpClient.Response.Locked) await OnBeforeResponse(sessionArgs);

            var response = sessionArgs.HttpClient.Response;
            closeConnection = !response.KeepAlive;

            if (!response.Locked)
            {
                // HTTP/2 forbids connection-specific header fields (RFC 7540 §8.1.2.2) that an HTTP/1.1
                // origin may legitimately send; EmitSyntheticResponseAsync already strips Transfer-Encoding
                // (h2 framing never uses it - length is implicit from DATA frames + END_STREAM), the rest
                // are stripped here.
                response.Headers.RemoveHeader(KnownHeaders.Connection);
                response.Headers.RemoveHeader("Keep-Alive");
                response.Headers.RemoveHeader(KnownHeaders.ProxyConnection);
                response.Headers.RemoveHeader(KnownHeaders.Upgrade);

                // RFC 7540 §8.1.2: header field names MUST be lowercase in HTTP/2. An HTTP/1.1 origin has no
                // such requirement (field names are case-insensitive on the wire), so the mixed-case names it
                // actually sent (e.g. "Content-Type") must be normalized here before Http2Helper.SendHeader
                // (invoked by EmitSyntheticResponseAsync below) HPACK-encodes them verbatim.
                LowercaseHeaderNames(response.Headers);
                if (response.HasTrailingHeaders) LowercaseHeaderNames(response.TrailingHeaders);

                var originConnection = connection;

                // Snapshot the origin's actual wire framing before EmitSyntheticResponseAsync (invoked below,
                // via RespondStreaming) strips Transfer-Encoding from response.Headers (h2 framing has no such
                // header) - re-reading response.HasBody/IsChunked/ContentLength from inside the writeBody
                // callback below after that point would see Transfer-Encoding already gone and (since
                // response.HttpVersion is still HTTP/1.1, never rewritten to 2.0 for this bridged response)
                // would wrongly conclude the response has no body at all.
                var originHasBody = response.HasBody;
                var originIsChunked = response.IsChunked;
                var originContentLength = response.ContentLength;

                sessionArgs.RespondStreaming(response, async (bodyStream, bodyCancellationToken) =>
                {
                    if (!originHasBody) return;

                    // Decodes the origin's actual wire framing (chunked or Content-Length-bounded) into raw
                    // body bytes; h2 DATA frames need no framing of their own (length is implicit), and
                    // Content-Encoding (if any) is left untouched and forwarded as-is - decoding it is the
                    // h2 client's job, exactly as it would be for a real h2 origin.
                    IHttpStreamReader reader = originConnection.Stream;
                    using var limited = new LimitedStream(reader, BufferPool, originIsChunked,
                        originContentLength, response.TrailingHeaders);
                    var buffer = BufferPool.GetBuffer();
                    try
                    {
                        int read;
                        while ((read = await limited.ReadAsync(buffer, 0, buffer.Length, bodyCancellationToken)) > 0)
                            await bodyStream.WriteAsync(buffer, 0, read, bodyCancellationToken);

                        await limited.Finish();
                    }
                    finally
                    {
                        BufferPool.ReturnBuffer(buffer);
                    }
                });
            }

            await Http2Helper.EmitSyntheticResponseAsync(sessionArgs, streamId, connectionState, clientStream,
                cancellationToken);
        }
        catch (Exception ex)
        {
            closeConnection = true;

            // A stream/connection cancellation (RST_STREAM, GOAWAY, or the client connection itself ending)
            // is an expected teardown path, not a bug - and the client is not waiting for an error response
            // in that case either. Only report and attempt to answer genuine origin-round-trip failures.
            if (!cancellationToken.IsCancellationRequested)
            {
                ExceptionFunc?.Invoke(new ProxyHttpException(
                    $"HTTP/2-to-HTTP/1.1 bridge origin round trip failed for stream {streamId}", ex, sessionArgs));

                try
                {
                    if (!sessionArgs.HttpClient.Response.Locked)
                    {
                        // headers not sent yet - answer with a clean synthetic error response, matching how
                        // a normal forwarded request that fails to connect/negotiate is reported elsewhere
                        // (see the ProxyConnectException call sites in Http2NegotiationHandler).
                        sessionArgs.GenericResponse($"Bad Gateway. {ex.Message}", HttpStatusCode.BadGateway);
                        await Http2Helper.EmitSyntheticResponseAsync(sessionArgs, streamId, connectionState,
                            clientStream, CancellationToken.None);
                    }
                    else
                    {
                        // headers (and maybe part of the body) already reached the client before the origin
                        // round trip failed (e.g. the origin dropped the connection mid-body) - an
                        // already-sent HEADERS frame cannot be replaced, so the best this can do is tell the
                        // client the stream ended abnormally instead of silently truncating the body.
                        await connectionState.ClientWriteLock.WaitAsync(CancellationToken.None);
                        try
                        {
                            await Http2Helper.SendRstStreamAsync(new Http2FrameHeader(), new byte[9], streamId,
                                Http2ErrorCode.InternalError, clientStream);
                        }
                        finally
                        {
                            connectionState.ClientWriteLock.Release();
                        }
                    }
                }
                catch
                {
                    // best-effort error reporting only - if the client connection itself is already gone
                    // there is nothing further to do; Http2Helper.SendHttp2's own teardown handles cleanup.
                }
            }
        }
        finally
        {
            if (connection != null) await TcpConnectionFactory.Release(connection, closeConnection);

            // Finalize (AfterResponse + Dispose) this stream immediately rather than deferring to connection
            // teardown: unlike a normally forwarded request, a bridged stream's response never flows through
            // CopyHttp2FrameAsync's isClient=false direction, so the generic "both directions closed"
            // bookkeeping there never observes it - without this, the stream would only ever be finalized
            // once the whole (potentially long-lived, multiplexed) h2 connection itself ends.
            // Http2StreamState.FinalizedFlag (checked inside FinalizeStreamAsync) makes this race-safe
            // against RST_STREAM/GOAWAY teardown finalizing the very same stream first.
            if (connectionState.Streams.TryRemove(streamId, out var finalStreamState))
            {
                connectionState.ClientSendFlow.RemoveStream(streamId);
                connectionState.ServerSendFlow.RemoveStream(streamId);
                await Http2Helper.FinalizeStreamAsync(finalStreamState,
                    async args => { await OnAfterResponse(args); }, ExceptionFunc);
            }
        }
    }

    /// <summary>
    ///     Renames every header in <paramref name="headers" /> to its lowercase form in place, preserving values
    ///     and relative order. <see cref="HttpHeader.NameData" /> is get-only, so each header is removed and
    ///     re-added rather than mutated - safe here because <see cref="HeaderCollection" />'s name lookups are
    ///     already case-insensitive (see its <c>StringComparer.OrdinalIgnoreCase</c> dictionaries), so no other
    ///     header access is affected by the rename.
    /// </summary>
    private static void LowercaseHeaderNames(HeaderCollection headers)
    {
        var originalHeaders = headers.ToList();
        headers.Clear();
        foreach (var header in originalHeaders)
        {
            headers.AddHeader(header.Name.ToLowerInvariant(), header.Value);
        }
    }
}
#endif
