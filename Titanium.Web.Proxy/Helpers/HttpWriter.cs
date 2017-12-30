using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using StreamExtended.Network;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Helpers
{
    abstract class HttpWriter : StreamWriter
    {
        protected HttpWriter(Stream stream, int bufferSize, bool leaveOpen) 
            : base(stream, Encoding.ASCII, bufferSize, leaveOpen)
        {
            NewLine = ProxyConstants.NewLine;
        }

        public void WriteHeaders(HeaderCollection headers, bool flush = true)
        {
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    header.WriteToStream(this);
                }
            }

            WriteLine();
            if (flush)
            {
                Flush();
            }
        }

        /// <summary>
        /// Write the headers to client
        /// </summary>
        /// <param name="headers"></param>
        /// <param name="flush"></param>
        /// <returns></returns>
        public async Task WriteHeadersAsync(HeaderCollection headers, bool flush = true)
        {
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    await header.WriteToStreamAsync(this);
                }
            }

            await WriteLineAsync();
            if (flush)
            {
                await FlushAsync();
            }
        }

        public async Task WriteAsync(byte[] data, bool flush = false)
        {
            await FlushAsync();
            await BaseStream.WriteAsync(data, 0, data.Length);
            if (flush)
            {
                // flush the stream and the encoder, too
                await FlushAsync();
            }
        }

        public async Task WriteAsync(byte[] data, int offset, int count, bool flush = false)
        {
            await FlushAsync();
            await BaseStream.WriteAsync(data, offset, count);
            if (flush)
            {
                // flush the stream and the encoder, too
                await FlushAsync();
            }
        }

        /// <summary>
        /// Writes the byte array body to the stream; optionally chunked
        /// </summary>
        /// <param name="data"></param>
        /// <param name="isChunked"></param>
        /// <returns></returns>
        internal async Task WriteBodyAsync(byte[] data, bool isChunked)
        {
            if (isChunked)
            {
                await WriteBodyChunkedAsync(data);
            }
            else
            {
                await WriteAsync(data);
            }
        }

        /// <summary>
        /// Copies the specified content length number of bytes to the output stream from the given inputs stream
        /// optionally chunked
        /// </summary>
        /// <param name="streamReader"></param>
        /// <param name="isChunked"></param>
        /// <param name="contentLength"></param>
        /// <returns></returns>
        internal async Task CopyBodyAsync(CustomBinaryReader streamReader, bool isChunked, long contentLength)
        {
            if (isChunked)
            {
                //Need to revist, find any potential bugs
                //send the body bytes to server in chunks
                await CopyBodyChunkedAsync(streamReader);
            }
            else
            {
                //http 1.0
                if (contentLength == -1)
                {
                    contentLength = long.MaxValue;
                }

                await CopyBytesFromStream(streamReader, contentLength);
            }
        }

        /// <summary>
        /// Copies the given input bytes to output stream chunked
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        private async Task WriteBodyChunkedAsync(byte[] data)
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
        /// <param name="reader"></param>
        /// <returns></returns>
        private async Task CopyBodyChunkedAsync(CustomBinaryReader reader)
        {
            while (true)
            {
                string chunkHead = await reader.ReadLineAsync();
                int chunkSize = int.Parse(chunkHead, NumberStyles.HexNumber);

                await WriteLineAsync(chunkHead);
                

                if (chunkSize != 0)
                {
                    await CopyBytesFromStream(reader, chunkSize);
                }

                await WriteLineAsync();

                //chunk trail
                await reader.ReadLineAsync();

                if (chunkSize == 0)
                {
                    break;
                }
            }
        }

        private async Task CopyBytesFromStream(CustomBinaryReader reader, long count)
        {
            await FlushAsync();
            await reader.CopyBytesToStream(BaseStream, count);
        }
    }
}
