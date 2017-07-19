using StreamExtended.Network;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Shared;

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
            await WriteResponseStatusAsync(response.HttpVersion, response.ResponseStatusCode, response.ResponseStatusDescription);
            response.ResponseHeaders.FixProxyHeaders();
            await WriteHeadersAsync(response.ResponseHeaders, flush);
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
        /// <param name="bufferSize"></param>
        /// <param name="inStreamReader"></param>
        /// <param name="isChunked"></param>
        /// <param name="contentLength"></param>
        /// <returns></returns>
        internal async Task WriteResponseBodyAsync(int bufferSize, CustomBinaryReader inStreamReader, bool isChunked,
            long contentLength)
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
                await WriteResponseBodyChunkedAsync(inStreamReader);
            }
        }

        /// <summary>
        /// Copies the streams chunked
        /// </summary>
        /// <param name="inStreamReader"></param>
        /// <returns></returns>
        internal async Task WriteResponseBodyChunkedAsync(CustomBinaryReader inStreamReader)
        {
            while (true)
            {
                string chunkHead = await inStreamReader.ReadLineAsync();
                int chunkSize = int.Parse(chunkHead, NumberStyles.HexNumber);

                if (chunkSize != 0)
                {
                    var chunkHeadBytes = Encoding.ASCII.GetBytes(chunkSize.ToString("x2"));
                    await BaseStream.WriteAsync(chunkHeadBytes, 0, chunkHeadBytes.Length);
                    await BaseStream.WriteAsync(ProxyConstants.NewLineBytes, 0, ProxyConstants.NewLineBytes.Length);

                    await inStreamReader.CopyBytesToStream(BaseStream, chunkSize);
                    await BaseStream.WriteAsync(ProxyConstants.NewLineBytes, 0, ProxyConstants.NewLineBytes.Length);

                    await inStreamReader.ReadLineAsync();
                }
                else
                {
                    await inStreamReader.ReadLineAsync();
                    await BaseStream.WriteAsync(ProxyConstants.ChunkEnd, 0, ProxyConstants.ChunkEnd.Length);
                    break;
                }
            }
        }

        /// <summary>
        /// Copies the given input bytes to output stream chunked
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        internal async Task WriteResponseBodyChunkedAsync(byte[] data)
        {
            var chunkHead = Encoding.ASCII.GetBytes(data.Length.ToString("x2"));

            await BaseStream.WriteAsync(chunkHead, 0, chunkHead.Length);
            await BaseStream.WriteAsync(ProxyConstants.NewLineBytes, 0, ProxyConstants.NewLineBytes.Length);
            await BaseStream.WriteAsync(data, 0, data.Length);
            await BaseStream.WriteAsync(ProxyConstants.NewLineBytes, 0, ProxyConstants.NewLineBytes.Length);

            await BaseStream.WriteAsync(ProxyConstants.ChunkEnd, 0, ProxyConstants.ChunkEnd.Length);
        }
    }
}
