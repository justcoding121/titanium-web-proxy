using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Helpers
{
    internal sealed class HttpRequestWriter : HttpWriter
    {
        internal HttpRequestWriter(Stream stream, int bufferSize) : base(stream, bufferSize)
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
