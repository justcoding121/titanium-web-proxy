using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Helpers
{
    internal sealed class HttpRequestWriter : HttpWriter
    {
        internal HttpRequestWriter(Stream stream, IBufferPool bufferPool, int bufferSize) 
            : base(stream, bufferPool, bufferSize)
        {
        }

        /// <summary>
        ///     Writes the request.
        /// </summary>
        /// <param name="request">The request object.</param>
        /// <param name="flush">Should we flush after write?</param>
        /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
        /// <returns></returns>
        internal async Task WriteRequestAsync(Request request, bool flush = true,
            CancellationToken cancellationToken = default)
        {
            await WriteLineAsync(Request.CreateRequestLine(request.Method, request.OriginalUrl, request.HttpVersion),
                cancellationToken);
            await WriteAsync(request, flush, cancellationToken);
        }
    }
}
