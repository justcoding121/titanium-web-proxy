using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using StreamExtended.Network;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Helpers
{
    sealed class HttpResponseWriter : HttpWriter
    {
        public HttpResponseWriter(Stream stream, int bufferSize) 
            : base(stream, bufferSize, true)
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
            response.Headers.FixProxyHeaders();
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
            return WriteLineAsync($"HTTP/{version.Major}.{version.Minor} {code} {description}");
        }
    }
}
