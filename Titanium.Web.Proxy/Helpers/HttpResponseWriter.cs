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

        /// <summary>
        /// Writes the byte array body to the stream; optionally chunked
        /// </summary>
        /// <param name="data"></param>
        /// <param name="isChunked"></param>
        /// <returns></returns>
        internal async Task WriteResponseBodyAsync(byte[] data, bool isChunked)
        {
            if (!isChunked)
            {
                await BaseStream.WriteAsync(data, 0, data.Length);
            }
            else
            {
                await WriteResponseBodyChunkedAsync(data);
            }
        }

        /// <summary>
        /// Copies the specified content length number of bytes to the output stream from the given inputs stream
        /// optionally chunked
        /// </summary>
        /// <param name="inStreamReader"></param>
        /// <param name="isChunked"></param>
        /// <param name="contentLength"></param>
        /// <returns></returns>
        internal async Task CopyBodyAsync(CustomBinaryReader inStreamReader, bool isChunked, long contentLength)
        {
            if (!isChunked)
            {
                //http 1.0
                if (contentLength == -1)
                {
                    contentLength = long.MaxValue;
                }

                await inStreamReader.CopyBytesToStream(BaseStream, contentLength);
            }
            else
            {
                await CopyBodyChunkedAsync(inStreamReader);
            }
        }

        /// <summary>
        /// Copies the given input bytes to output stream chunked
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        private async Task WriteResponseBodyChunkedAsync(byte[] data)
        {
            var chunkHead = Encoding.ASCII.GetBytes(data.Length.ToString("x2"));

            await WriteAsync(chunkHead);
            await WriteLineAsync();
            await WriteAsync(data);
            await WriteLineAsync();

            await WriteLineAsync("0");
            await WriteLineAsync();
        }

        /// <summary>
        /// Copies the streams chunked
        /// </summary>
        /// <param name="inStreamReader"></param>
        /// <returns></returns>
        private async Task CopyBodyChunkedAsync(CustomBinaryReader inStreamReader)
        {
            while (true)
            {
                string chunkHead = await inStreamReader.ReadLineAsync();
                int chunkSize = int.Parse(chunkHead, NumberStyles.HexNumber);

                await WriteLineAsync(chunkHead);

                if (chunkSize != 0)
                {
                    await inStreamReader.CopyBytesToStream(BaseStream, chunkSize);
                }

                await WriteLineAsync();
                await inStreamReader.ReadLineAsync();

                if (chunkSize == 0)
                {
                    break;
                }
            }
        }
    }
}
