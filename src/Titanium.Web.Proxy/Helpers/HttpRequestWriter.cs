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
        /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
        /// <returns></returns>
        internal async Task WriteRequestAsync(Request request, CancellationToken cancellationToken = default)
        {
            var headerBuilder = new HeaderBuilder();
            headerBuilder.WriteRequestLine(request.Method, request.Url, request.HttpVersion);
            await WriteAsync(request, headerBuilder, cancellationToken);
        }
    }
}
