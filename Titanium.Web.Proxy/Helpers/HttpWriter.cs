using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using StreamExtended.Helpers;
using StreamExtended.Network;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Helpers
{
    abstract class HttpWriter
    {
        public int BufferSize { get; }

        private readonly Stream stream;

        private readonly char[] charBuffer;

        private static readonly byte[] newLine = ProxyConstants.NewLine;
        
        private static readonly Encoder encoder = Encoding.ASCII.GetEncoder();

        protected HttpWriter(Stream stream, int bufferSize) 
        {
            BufferSize = bufferSize;

            // ASCII encoder max byte count is char count + 1
            charBuffer = new char[BufferSize - 1];
            this.stream = stream;
        }

        public Task WriteLineAsync()
        {
            return WriteAsync(newLine);
        }

        public async Task WriteAsync(string value)
        {
            int charCount = value.Length;
            value.CopyTo(0, charBuffer, 0, charCount);

            if (charCount < BufferSize)
            {
                var buffer = BufferPool.GetBuffer(BufferSize);
                try
                {
                    int idx = encoder.GetBytes(charBuffer, 0, charCount, buffer, 0, true);
                    await WriteAsync(buffer, 0, idx);
                }
                finally
                {
                    BufferPool.ReturnBuffer(buffer);
                }
            }
            else
            {
                var buffer = new byte[charCount + 1];
                int idx = encoder.GetBytes(charBuffer, 0, charCount, buffer, 0, true);
                await WriteAsync(buffer, 0, idx);
            }
        }

        public async Task WriteLineAsync(string value)
        {
            await WriteAsync(value);
            await WriteLineAsync();
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
                // flush the stream
                await stream.FlushAsync();
            }
        }

        public async Task WriteAsync(byte[] data, bool flush = false)
        {
            await stream.WriteAsync(data, 0, data.Length);
            if (flush)
            {
                // flush the stream
                await stream.FlushAsync();
            }
        }

        public async Task WriteAsync(byte[] data, int offset, int count, bool flush = false)
        {
            await stream.WriteAsync(data, offset, count);
            if (flush)
            {
                // flush the stream
                await stream.FlushAsync();
            }
        }

        /// <summary>
        /// Writes the byte array body to the stream; optionally chunked
        /// </summary>
        /// <param name="data"></param>
        /// <param name="isChunked"></param>
        /// <returns></returns>
        internal Task WriteBodyAsync(byte[] data, bool isChunked)
        {
            if (isChunked)
            {
                return WriteBodyChunkedAsync(data);
            }

            return WriteAsync(data);
        }

        /// <summary>
        /// Copies the specified content length number of bytes to the output stream from the given inputs stream
        /// optionally chunked
        /// </summary>
        /// <param name="streamReader"></param>
        /// <param name="isChunked"></param>
        /// <param name="contentLength"></param>
        /// <returns></returns>
        internal Task CopyBodyAsync(CustomBinaryReader streamReader, bool isChunked, long contentLength)
        {
            if (isChunked)
            {
                //Need to revist, find any potential bugs
                //send the body bytes to server in chunks
                return CopyBodyChunkedAsync(streamReader);
            }
            
            //http 1.0
            if (contentLength == -1)
            {
                contentLength = long.MaxValue;
            }

            return CopyBytesFromStream(streamReader, contentLength);
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

        private Task CopyBytesFromStream(CustomBinaryReader reader, long count)
        {
            return reader.CopyBytesToStream(stream, count);
        }
    }
}
