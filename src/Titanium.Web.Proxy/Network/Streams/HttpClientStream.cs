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
        var headerBuilder = new HeaderBuilder();

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

    internal async ValueTask<RequestStatusInfo> ReadRequestLine(CancellationToken cancellationToken = default)
    {
        // read the first line HTTP command
        var httpCmd = await ReadLineAsync(cancellationToken);
        if (string.IsNullOrEmpty(httpCmd)) return default;

        Request.ParseRequestLine(httpCmd!, out var method, out var requestUri, out var version);

        return new RequestStatusInfo { Method = method, RequestUri = requestUri, Version = version };
    }
}