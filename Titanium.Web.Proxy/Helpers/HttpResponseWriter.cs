using System;
using System.IO;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Helpers
{
    sealed class HttpResponseWriter : HttpWriter
    {
        public HttpResponseWriter(Stream stream, int bufferSize) : base(stream, bufferSize)
        {
        }

        /// <summary>
        /// Writes the response.
        /// </summary>
        /// <param name="response"></param>
        /// <param name="flush"></param>
        /// <returns></returns>
        public async Task WriteResponseAsync(Response response, bool flush = true)
        {
            await WriteResponseStatusAsync(response.HttpVersion, response.StatusCode, response.StatusDescription);
            await WriteHeadersAsync(response.Headers, flush);
        }

        /// <summary>
        /// Write response status
        /// </summary>
        /// <param name="version"></param>
        /// <param name="code"></param>
        /// <param name="description"></param>
        /// <returns></returns>
        public Task WriteResponseStatusAsync(Version version, int code, string description)
        {
            return WriteLineAsync(Response.CreateResponseLine(version, code, description));
        }
    }
}
