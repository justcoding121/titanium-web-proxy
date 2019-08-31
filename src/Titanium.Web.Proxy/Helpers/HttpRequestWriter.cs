using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.Helpers
{
    internal sealed class HttpRequestWriter : HttpWriter
    {
        internal HttpRequestWriter(Stream stream, IBufferPool bufferPool) 
            : base(stream, bufferPool)
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
            await WriteLineAsync(Request.CreateRequestLine(request.Method, request.RequestUriString, request.HttpVersion),
                cancellationToken);
            await WriteAsync(request, flush, cancellationToken);
        }
    }
}
