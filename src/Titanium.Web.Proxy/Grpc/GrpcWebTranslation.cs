using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.Grpc;

/// <summary>
/// gRPC-Web to gRPC translation for a route with <c>grpcWeb: true</c>. Binary unary and server-streaming calls
/// only. The request keeps its length-prefixed body and only changes its content type. The response relays the
/// origin DATA frames and ends with one trailer frame (flag <c>0x80</c>), because a browser cannot read
/// HTTP/2 trailers.
/// </summary>
internal static class GrpcWebTranslation
{
    /// <summary>
    /// Rewrites a matching request for an HTTP/2 origin. True when the session is now translated, which
    /// makes <see cref="TranslateResponseAsync"/> shape the answer. Safe to call again on a replay.
    /// </summary>
    internal static bool TryTranslateRequest(SessionEventArgs args)
    {
        if (args.MatchedRoute is not { GrpcWeb: true })
        {
            return false;
        }

        if (args.GrpcWebClientContentType is not null)
        {
            return true;
        }

        var request = args.HttpClient.Request;
        var contentType = request.ContentType;
        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) ||
            !GrpcWebAdapter.IsGrpcWebBinary(contentType))
        {
            return false;
        }

        // The client's own media type is answered back, so a client that sent no +proto gets none.
        var separator = contentType!.IndexOf(';');
        args.GrpcWebClientContentType = (separator < 0 ? contentType : contentType[..separator]).Trim();

        request.ContentType = GrpcWebAdapter.GrpcContentType;
        request.Headers.RemoveHeader("te");
        request.Headers.AddHeader("te", "trailers");
        request.Headers.RemoveHeader("x-grpc-web");
        return true;
    }

    /// <summary>
    /// Turns a native gRPC answer into a gRPC-Web one: content type, a trailer frame after the last message,
    /// and no HTTP trailers. Leaves any other answer (an origin error page, a non-gRPC reply) untouched.
    /// </summary>
    internal static async ValueTask TranslateResponseAsync(SessionEventArgs args, Http2OriginExchange exchange,
        CancellationToken cancellationToken)
    {
        if (args.GrpcWebClientContentType is not { } clientType)
        {
            return;
        }

        var response = exchange.Response;
        if (!GrpcWebAdapter.IsNativeGrpc(response.ContentType))
        {
            return;
        }

        response.ContentType = clientType;

        var originTrailers = exchange.TrailingHeaders;
        exchange.TrailingHeaders = null;
        if (response.HasTrailingHeaders)
        {
            response.TrailingHeaders.Clear();
        }

        if (response.StreamBodyWriter is not { } inner)
        {
            // A buffered body can be complete before the trailers that follow it have arrived.
            if (exchange.LateTrailerSource is { IsInboundComplete: false } source)
            {
                await source.WhenInboundCompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            originTrailers = exchange.LateTrailerSource?.CurrentTrailers ?? originTrailers;
            AppendTrailerFrameToBufferedBody(response, exchange, originTrailers);
            return;
        }

        // The body grows by the trailer frame, so the origin length no longer holds.
        response.ContentLength = -1;
        response.StreamBodyWriter = async (destination, cancellationToken) =>
        {
            await inner(destination, cancellationToken).ConfigureAwait(false);

            // Trailers that arrive after the last DATA frame are only known once the body has drained.
            var trailers = exchange.LateTrailerSource?.CurrentTrailers ?? originTrailers;
            await WriteTrailerFrameAsync(destination, TrailerSource(response, trailers), cancellationToken)
                .ConfigureAwait(false);
        };
    }

    private static void AppendTrailerFrameToBufferedBody(Response response, Http2OriginExchange exchange,
        HeaderCollection? originTrailers)
    {
        var hasTrailers = originTrailers is not null && originTrailers.Any();
        if (hasTrailers)
        {
            // The status is known before headers go out, so a client that only reads headers still sees it.
            GrpcWebAdapter.PromoteTrailersToHeaders(originTrailers!, response.Headers);
        }

        var frame = GrpcWebAdapter.EncodeTrailerFrame(TrailerSource(response, originTrailers));
        var body = new byte[exchange.Body.Length + frame.Length];
        exchange.Body.CopyTo(body, 0);
        frame.CopyTo(body, exchange.Body.Length);
        exchange.Body = body;
        response.ContentLength = body.Length;
    }

    /// <summary>
    /// The origin's trailers, or for a trailers-only answer (status sent in the headers, no trailing HEADERS)
    /// the <c>grpc-*</c> response headers.
    /// </summary>
    private static IEnumerable<HttpHeader> TrailerSource(Response response, HeaderCollection? trailers)
    {
        if (trailers is not null && trailers.Any())
        {
            return trailers;
        }

        return response.Headers.Where(header =>
            header.Name.StartsWith("grpc-", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static async Task WriteTrailerFrameAsync(Stream destination, IEnumerable<HttpHeader> trailers,
        CancellationToken cancellationToken)
    {
        var frame = GrpcWebAdapter.EncodeTrailerFrame(trailers);
        await destination.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }
}
