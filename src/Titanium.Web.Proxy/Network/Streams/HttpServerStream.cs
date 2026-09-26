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
    internal ValueTask WriteRequestAsync(Request request, CancellationToken cancellationToken = default)
    {
        var headerBuilder = HeaderBuilder.Rent();
        try
        {
            headerBuilder.WriteRequestLine(request.Method, request.RequestUriString, request.HttpVersion);
            var writeVt = WriteAsync(request, headerBuilder, cancellationToken);
            if (writeVt.IsCompletedSuccessfully)
            {
                HeaderBuilder.Return(headerBuilder);
                return default;
            }

            return AwaitAndReturnHeaderBuilder(writeVt, headerBuilder);
        }
        catch
        {
            HeaderBuilder.Return(headerBuilder);
            throw;
        }
    }

    /// <summary>
    ///     Reads the HTTP response status line.
    /// </summary>
    /// <returns>
    ///     The parsed status info, or <c>null</c> when the peer closed the connection before sending
    ///     any status line (normal EOF / keep-alive idle close). Malformed status lines still throw.
    /// </returns>
    internal ValueTask<ResponseStatusInfo?> ReadResponseStatus(CancellationToken cancellationToken = default)
    {
        if (TryParseResponseLineFromBuffer(out var version, out var statusCode, out var description,
                out var emptyLine))
        {
            if (!emptyLine)
            {
                return new ValueTask<ResponseStatusInfo?>(new ResponseStatusInfo
                {
                    Version = version, StatusCode = statusCode, Description = description
                });
            }

            // Blank line before status — try again from the buffer, else fill.
            if (TryParseResponseLineFromBuffer(out version, out statusCode, out description, out emptyLine)
                && !emptyLine)
            {
                return new ValueTask<ResponseStatusInfo?>(new ResponseStatusInfo
                {
                    Version = version, StatusCode = statusCode, Description = description
                });
            }
        }

        return ReadResponseStatusFillAsync(cancellationToken);
    }

    private async ValueTask<ResponseStatusInfo?> ReadResponseStatusFillAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!await FillBufferAsync(cancellationToken))
                return null;

            if (!TryParseResponseLineFromBuffer(out var version, out var statusCode, out var description,
                    out var emptyLine))
                continue;

            if (emptyLine)
                continue;

            return new ResponseStatusInfo { Version = version, StatusCode = statusCode, Description = description };
        }
    }

    private static async ValueTask AwaitAndReturnHeaderBuilder(ValueTask writeVt, HeaderBuilder headerBuilder)
    {
        try
        {
            await writeVt;
        }
        finally
        {
            HeaderBuilder.Return(headerBuilder);
        }
    }
}
