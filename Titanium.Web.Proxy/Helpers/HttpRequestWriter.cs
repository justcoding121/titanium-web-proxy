using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Helpers
{
    internal sealed class HttpRequestWriter : HttpWriter
    {
        public HttpRequestWriter(Stream stream, int bufferSize) : base(stream, bufferSize)
        {
        }

        /// <summary>
        ///     Writes the request.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="flush"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task WriteRequestAsync(Request request, bool flush = true,
            CancellationToken cancellationToken = default)
        {
            await WriteLineAsync(Request.CreateRequestLine(request.Method, request.OriginalUrl, request.HttpVersion),
                cancellationToken);
            await WriteAsync(request, flush, cancellationToken);
        }
    }
}
