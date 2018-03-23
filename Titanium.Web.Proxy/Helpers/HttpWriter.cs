using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using StreamExtended.Helpers;
using StreamExtended.Network;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Helpers
{
    internal class HttpWriter : CustomBinaryWriter
    {
        public int BufferSize { get; }

        private readonly char[] charBuffer;

        private static readonly byte[] newLine = ProxyConstants.NewLine;

        private static readonly Encoder encoder = Encoding.ASCII.GetEncoder();

        internal HttpWriter(Stream stream, int bufferSize) : base(stream)
        {
            BufferSize = bufferSize;

            // ASCII encoder max byte count is char count + 1
            charBuffer = new char[BufferSize - 1];
        }

        public Task WriteLineAsync()
        {
            return WriteAsync(newLine);
        }

        public Task WriteAsync(string value)
        {
            return WriteAsyncInternal(value, false);
        }

        private Task WriteAsyncInternal(string value, bool addNewLine)
        {
            int newLineChars = addNewLine ? newLine.Length : 0;
            int charCount = value.Length;
            if (charCount < BufferSize - newLineChars)
            {
                value.CopyTo(0, charBuffer, 0, charCount);

                var buffer = BufferPool.GetBuffer(BufferSize);
                try
                {
                    int idx = encoder.GetBytes(charBuffer, 0, charCount, buffer, 0, true);
                    if (newLineChars > 0)
                    {
                        Buffer.BlockCopy(newLine, 0, buffer, idx, newLineChars);
                        idx += newLineChars;
                    }

                    return WriteAsync(buffer, 0, idx);
                }
                finally
                {
                    BufferPool.ReturnBuffer(buffer);
                }
            }
            else
            {
                var charBuffer = new char[charCount];
                value.CopyTo(0, charBuffer, 0, charCount);

                var buffer = new byte[charCount + newLineChars + 1];
                int idx = encoder.GetBytes(charBuffer, 0, charCount, buffer, 0, true);
                if (newLineChars > 0)
                {
                    Buffer.BlockCopy(newLine, 0, buffer, idx, newLineChars);
                    idx += newLineChars;
                }

                return WriteAsync(buffer, 0, idx);
            }
        }

        public Task WriteLineAsync(string value)
        {
            return WriteAsyncInternal(value, true);
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
                await FlushAsync();
            }
        }

        public async Task WriteAsync(byte[] data, bool flush = false)
        {
            await WriteAsync(data, 0, data.Length);
            if (flush)
            {
                // flush the stream
                await FlushAsync();
            }
        }

        public async Task WriteAsync(byte[] data, int offset, int count, bool flush)
        {
            await WriteAsync(data, offset, count);
            if (flush)
            {
                // flush the stream
                await FlushAsync();
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
        /// <param name="removeChunkedEncoding"></param>
        /// <param name="onCopy"></param>
        /// <returns></returns>
        internal Task CopyBodyAsync(CustomBinaryReader streamReader, bool isChunked, long contentLength, bool removeChunkedEncoding, Action<byte[], int, int> onCopy)
        {
            //For chunked request we need to read data as they arrive, until we reach a chunk end symbol
            if (isChunked)
            {
                //Need to revist, find any potential bugs
                //send the body bytes to server in chunks
                return CopyBodyChunkedAsync(streamReader, removeChunkedEncoding, onCopy);
            }
            
            //http 1.0
            if (contentLength == -1)
            {
                contentLength = long.MaxValue;
            }

            //If not chunked then its easy just read the amount of bytes mentioned in content length header
            return CopyBytesFromStream(streamReader, contentLength, onCopy);
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
        /// <param name="removeChunkedEncoding"></param>
        /// <param name="onCopy"></param>
        /// <returns></returns>
        private async Task CopyBodyChunkedAsync(CustomBinaryReader reader, bool removeChunkedEncoding, Action<byte[], int, int> onCopy)
        {
            while (true)
            {
                string chunkHead = await reader.ReadLineAsync();
                int chunkSize = int.Parse(chunkHead, NumberStyles.HexNumber);

                if (!removeChunkedEncoding)
                {
                    await WriteLineAsync(chunkHead);
                }

                if (chunkSize != 0)
                {
                    await CopyBytesFromStream(reader, chunkSize, onCopy);
                }

                if (!removeChunkedEncoding)
                {
                    await WriteLineAsync();
                }

                //chunk trail
                await reader.ReadLineAsync();

                if (chunkSize == 0)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Copies the specified bytes to the stream from the input stream
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="count"></param>
        /// <param name="onCopy"></param>
        /// <returns></returns>
        private async Task CopyBytesFromStream(CustomBinaryReader reader, long count, Action<byte[], int, int> onCopy)
        {
            var buffer = reader.Buffer;
            long remainingBytes = count;

            while (remainingBytes > 0)
            {
                int bytesToRead = buffer.Length;
                if (remainingBytes < bytesToRead)
                {
                    bytesToRead = (int)remainingBytes;
                }

                int bytesRead = await reader.ReadBytesAsync(buffer, bytesToRead);
                if (bytesRead == 0)
                {
                    break;
                }

                remainingBytes -= bytesRead;

                await WriteAsync(buffer, 0, bytesRead);

                onCopy?.Invoke(buffer, 0, bytesRead);
            }
        }
    }
}
