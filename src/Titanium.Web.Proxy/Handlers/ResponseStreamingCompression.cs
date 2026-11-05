using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy;

/// <summary>
///     Streaming response compression (<c>server.responseCompression.mode: streaming</c>): the origin body is
///     compressed while it is relayed to an HTTP/1.1 client, or to an HTTP/2 client on the HTTP/2-to-HTTP/1.1
///     bridge. Runs after <c>BeforeResponse</c>, so handlers see the origin's own headers.
/// </summary>
public partial class ProxyServer
{
    /// <summary>
    ///     What the relay needs once <see cref="TryBeginStreamingCompression" /> rewrote the response headers: the
    ///     codec, and the origin's framing, captured before the rewrite removed Content-Length.
    /// </summary>
    internal readonly record struct StreamingCompressionPlan(HttpCompression Algorithm, bool OriginChunked,
        long OriginLength);

    /// <summary>
    ///     Decides whether this response is compressed while it is relayed and, if so, rewrites its headers
    ///     (Content-Encoding, chunked framing, Vary, weak ETag). Call after <c>BeforeResponse</c> and before the
    ///     headers are written. Returns null, leaving the response untouched, when any eligibility rule fails or
    ///     when a feature that needs the plain bytes is in use (a body-write hook, a buffered or user-produced body).
    /// </summary>
    private StreamingCompressionPlan? TryBeginStreamingCompression(SessionEventArgs args,
        ResponseCompressionPolicy policy, bool clientIsHttp2)
    {
        var request = args.HttpClient.Request;
        var response = args.HttpClient.Response;

        if (response.IsBodyRead || response.IsSynthetic
            || response.Http3RawBodyDrain != null || !response.OriginalHasBody
            || response.HttpVersion.Major != 1 || request.HttpVersion.Major >= 3)
            return null;

        // An HTTP/1.0 client cannot take a chunked body.
        if (!clientIsHttp2 && request.HttpVersion == HttpHeader.Version10)
            return null;

        // The body-write hook exposes the wire bytes of the origin body one piece at a time.
        if (ShouldCallBeforeResponseBodyWrite())
            return null;

        if (ResponseCompressionPolicy.IsRouteOff(args.MatchedRoute))
            return null;

        if (!policy.TrySelectStreaming(request, response, out var algorithm))
            return null;

        // Framing as the origin sent it (possibly edited by BeforeResponse); ApplyStreaming drops it.
        var plan = new StreamingCompressionPlan(algorithm, response.IsChunked, response.ContentLength);
        ResponseCompressionPolicy.ApplyStreaming(response, algorithm);
        return plan;
    }

    /// <summary>
    ///     Reads the origin body, compresses it and writes it to <paramref name="sink" />, flushing on every origin
    ///     read. The sink write is awaited before the next read, so a slow client holds the origin back and memory
    ///     stays flat. Returns false when the client went away first; the origin connection is then marked for
    ///     closing, since its body is only partly read, and the caller must not store the body in the cache.
    /// </summary>
    private async Task<bool> RelayCompressedBodyAsync(SessionEventArgs args, ResponseCompressionPolicy policy,
        StreamingCompressionPlan plan, Stream sink, Func<bool>? clientGone, CancellationToken cancellationToken)
    {
        var response = args.HttpClient.Response;
        var limits = ResourceLimits;
        using var origin = new LimitedStream(args.HttpClient.Connection.Stream, BufferPool, plan.OriginChunked,
            plan.OriginLength, response.TrailingHeaders, limits.MaxTrailerHeaderCount,
            limits.MaxTrailerHeaderBlockBytes);

        var buffer = BufferPool.GetBuffer(StreamingResponseRelay.ReadBufferBytes);
        try
        {
            var completed = await StreamingResponseRelay.RelayAsync(origin, sink, plan.Algorithm, policy.Level, buffer,
                plan.OriginChunked ? -1 : plan.OriginLength, args.OnDataReceived, clientGone, cancellationToken)
                .ConfigureAwait(false);

            if (completed)
            {
                await origin.Finish().ConfigureAwait(false);
                return true;
            }

            args.HttpClient.CloseServerConnection = true;
            return false;
        }
        catch
        {
            // The body is partly read: the connection cannot carry another exchange.
            args.HttpClient.CloseServerConnection = true;
            throw;
        }
        finally
        {
            BufferPool.ReturnBuffer(buffer);
        }
    }
}
