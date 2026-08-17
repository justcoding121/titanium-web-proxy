using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.Helpers;

internal sealed class HttpClientStream : HttpStream
{
    internal HttpClientStream(ProxyServer server, TcpClientConnection connection, Stream stream, IBufferPool bufferPool,
        CancellationToken cancellationToken)
        : base(server, stream, bufferPool, cancellationToken)
    {
        Connection = connection;
    }

    public TcpClientConnection Connection { get; }

    /// <summary>
    ///     Writes the response.
    /// </summary>
    /// <param name="response">The response object.</param>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns>The Task.</returns>
    internal async ValueTask WriteResponseAsync(Response response, CancellationToken cancellationToken = default)
    {
        var headerBuilder = HeaderBuilder.Rent();
        try
        {
            // Write back response status to client
            headerBuilder.WriteResponseLine(response.HttpVersion, response.StatusCode, response.StatusDescription);

            // RFC 7231 §4.3.6: a successful (2xx) response to CONNECT establishes a tunnel and MUST NOT
            // include Content-Length or Transfer-Encoding — the byte stream that follows the header
            // terminator belongs to the tunnel, not to the response body.
            if (response is ConnectResponse && response.StatusCode >= 200 && response.StatusCode < 300)
            {
                response.Headers.RemoveHeader(KnownHeaders.ContentLength);
                response.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
            }

            await WriteAsync(response, headerBuilder, cancellationToken);
        }
        finally
        {
            HeaderBuilder.Return(headerBuilder);
        }
    }

    internal ValueTask<RequestStatusInfo> ReadRequestLine(CancellationToken cancellationToken = default)
    {
        var resultVt = ReadRequestLineWithResultAsync(cancellationToken);
        if (resultVt.IsCompletedSuccessfully)
        {
            var result = resultVt.Result;
            if (result.Cancelled)
                cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<RequestStatusInfo>(result.Status);
        }

        return ReadRequestLineSlow(resultVt, cancellationToken);
    }

    private static async ValueTask<RequestStatusInfo> ReadRequestLineSlow(
        ValueTask<(RequestStatusInfo Status, bool Cancelled)> resultVt, CancellationToken cancellationToken)
    {
        var result = await resultVt;
        if (result.Cancelled)
            cancellationToken.ThrowIfCancellationRequested();
        return result.Status;
    }

    /// <summary>
    ///     Reads the request line without throwing on cancellation (HTTP/1 session cancel hygiene).
    /// </summary>
    internal ValueTask<(RequestStatusInfo Status, bool Cancelled)> ReadRequestLineWithResultAsync(
        CancellationToken cancellationToken = default)
    {
        var lineVt = ReadLineWithResultAsync(cancellationToken);
        if (lineVt.IsCompletedSuccessfully)
            return new ValueTask<(RequestStatusInfo Status, bool Cancelled)>(
                ParseRequestLineResult(lineVt.Result));

        return ReadRequestLineWithResultSlowAsync(lineVt);
    }

    private static async ValueTask<(RequestStatusInfo Status, bool Cancelled)> ReadRequestLineWithResultSlowAsync(
        ValueTask<(string? Line, bool Cancelled)> lineVt)
    {
        return ParseRequestLineResult(await lineVt);
    }

    private static (RequestStatusInfo Status, bool Cancelled) ParseRequestLineResult(
        (string? httpCmd, bool cancelled) line)
    {
        if (line.cancelled) return (default, true);
        if (string.IsNullOrEmpty(line.httpCmd)) return (default, false);

        Request.ParseRequestLine(line.httpCmd, out var method, out var requestUri, out var version);

        return (new RequestStatusInfo { Method = method, RequestUri = requestUri, Version = version }, false);
    }
}
