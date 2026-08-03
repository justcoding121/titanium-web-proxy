using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.Helpers;

internal sealed class HttpServerStream : HttpStream
{
    protected override bool IsRetryableHeaderWriteFailure => true;

    internal HttpServerStream(ProxyServer server, Stream stream, IBufferPool bufferPool,
        CancellationToken cancellationToken)
        : base(server, stream, bufferPool, cancellationToken)
    {
    }

    /// <summary>
    ///     Writes the request.
    /// </summary>
    /// <param name="request">The request object.</param>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns></returns>
    internal async ValueTask WriteRequestAsync(Request request, CancellationToken cancellationToken = default)
    {
        var headerBuilder = new HeaderBuilder();
        headerBuilder.WriteRequestLine(request.Method, request.RequestUriString, request.HttpVersion);
        await WriteAsync(request, headerBuilder, cancellationToken);
    }

    /// <summary>
    ///     Reads the HTTP response status line.
    /// </summary>
    /// <returns>
    ///     The parsed status info, or <c>null</c> when the peer closed the connection before sending
    ///     any status line (normal EOF / keep-alive idle close). Malformed status lines still throw.
    /// </returns>
    internal async ValueTask<ResponseStatusInfo?> ReadResponseStatus(CancellationToken cancellationToken = default)
    {
        var httpStatus = await ReadLineAsync(cancellationToken);
        if (httpStatus == null)
            return null;

        if (httpStatus.Length == 0)
        {
            // A blank line before the status is unusual; read again. A subsequent EOF is still a normal close,
            // not a protocol error.
            httpStatus = await ReadLineAsync(cancellationToken);
            if (httpStatus == null)
                return null;
        }

        Response.ParseResponseLine(httpStatus, out var version, out var statusCode, out var description);
        return new ResponseStatusInfo { Version = version, StatusCode = statusCode, Description = description };
    }
}