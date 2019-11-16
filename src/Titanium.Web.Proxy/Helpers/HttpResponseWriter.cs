using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.Helpers
{
    internal sealed class HttpResponseWriter : HttpWriter
    {
        internal HttpResponseWriter(Stream stream, IBufferPool bufferPool) 
            : base(stream, bufferPool)
        {
        }

        /// <summary>
        ///     Writes the response.
        /// </summary>
        /// <param name="response">The response object.</param>
        /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
        /// <returns>The Task.</returns>
        internal async Task WriteResponseAsync(Response response, CancellationToken cancellationToken = default)
        {
            var headerBuilder = new HeaderBuilder();
            headerBuilder.WriteResponseLine(response.HttpVersion, response.StatusCode, response.StatusDescription);
            await WriteAsync(response, headerBuilder, cancellationToken);
        }
    }
}
