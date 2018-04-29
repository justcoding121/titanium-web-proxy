using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended.Helpers;
using StreamExtended.Network;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Helpers
{
    internal class HttpWriter : CustomBinaryWriter
    {
        private static readonly byte[] newLine = ProxyConstants.NewLine;

        private static readonly Encoder encoder = Encoding.ASCII.GetEncoder();

        private readonly char[] charBuffer;

        internal HttpWriter(Stream stream, int bufferSize) : base(stream)
        {
            BufferSize = bufferSize;

            // ASCII encoder max byte count is char count + 1
            charBuffer = new char[BufferSize - 1];
        }

        internal int BufferSize { get; }

        /// <summary>
        /// Writes a line async
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
        /// <returns></returns>
        internal Task WriteLineAsync(CancellationToken cancellationToken = default)
        {
            return WriteAsync(newLine, cancellationToken: cancellationToken);
        }

        internal Task WriteAsync(string value, CancellationToken cancellationToken = default)
        {
            return WriteAsyncInternal(value, false, cancellationToken);
        }

        private Task WriteAsyncInternal(string value, bool addNewLine, CancellationToken cancellationToken)
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

                    return WriteAsync(buffer, 0, idx, cancellationToken);
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

                return WriteAsync(buffer, 0, idx, cancellationToken);
            }
        }

        internal Task WriteLineAsync(string value, CancellationToken cancellationToken = default)
        {
            return WriteAsyncInternal(value, true, cancellationToken);
        }

        /// <summary>
        ///     Write the headers to client
        /// </summary>
        /// <param name="headers"></param>
        /// <param name="flush"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal async Task WriteHeadersAsync(HeaderCollection headers, bool flush = true, CancellationToken cancellationToken = default)
        {
            foreach (var header in headers)
            {
                await header.WriteToStreamAsync(this, cancellationToken);
            }

            await WriteLineAsync(cancellationToken);
            if (flush)
            {
                await FlushAsync(cancellationToken);
            }
        }

        internal async Task WriteAsync(byte[] data, bool flush = false, CancellationToken cancellationToken = default)
        {
            await WriteAsync(data, 0, data.Length, cancellationToken);
            if (flush)
            {
                await FlushAsync(cancellationToken);
            }
        }

        internal async Task WriteAsync(byte[] data, int offset, int count, bool flush, CancellationToken cancellationToken = default)
        {
            await WriteAsync(data, offset, count, cancellationToken);
            if (flush)
            {
                await FlushAsync(cancellationToken);
            }
        }

        /// <summary>
        ///     Writes the byte array body to the stream; optionally chunked
        /// </summary>
        /// <param name="data"></param>
        /// <param name="isChunked"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal Task WriteBodyAsync(byte[] data, bool isChunked, CancellationToken cancellationToken)
        {
            if (isChunked)
            {
                return WriteBodyChunkedAsync(data, cancellationToken);
            }

            return WriteAsync(data, cancellationToken: cancellationToken);
        }

        /// <summary>
        ///     Copies the specified content length number of bytes to the output stream from the given inputs stream
        ///     optionally chunked
        /// </summary>
        /// <param name="streamReader"></param>
        /// <param name="isChunked"></param>
        /// <param name="contentLength"></param>
        /// <param name="onCopy"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal Task CopyBodyAsync(CustomBinaryReader streamReader, bool isChunked, long contentLength,
            Action<byte[], int, int> onCopy, CancellationToken cancellationToken)
        {
            //For chunked request we need to read data as they arrive, until we reach a chunk end symbol
            if (isChunked)
            {
                return CopyBodyChunkedAsync(streamReader, onCopy, cancellationToken);
            }

            //http 1.0 or the stream reader limits the stream
            if (contentLength == -1)
            {
                contentLength = long.MaxValue;
            }

            //If not chunked then its easy just read the amount of bytes mentioned in content length header
            return CopyBytesFromStream(streamReader, contentLength, onCopy, cancellationToken);
        }

        /// <summary>
        ///     Copies the given input bytes to output stream chunked
        /// </summary>
        /// <param name="data"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task WriteBodyChunkedAsync(byte[] data, CancellationToken cancellationToken)
        {
            var chunkHead = Encoding.ASCII.GetBytes(data.Length.ToString("x2"));

            await WriteAsync(chunkHead, cancellationToken: cancellationToken);
            await WriteLineAsync(cancellationToken);
            await WriteAsync(data, cancellationToken: cancellationToken);
            await WriteLineAsync(cancellationToken);

            await WriteLineAsync("0", cancellationToken);
            await WriteLineAsync(cancellationToken);
        }

        /// <summary>
        ///     Copies the streams chunked
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="onCopy"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task CopyBodyChunkedAsync(CustomBinaryReader reader, Action<byte[], int, int> onCopy, CancellationToken cancellationToken)
        {
            while (true)
            {
                string chunkHead = await reader.ReadLineAsync(cancellationToken);
                int idx = chunkHead.IndexOf(";");
                if (idx >= 0)
                {
                    chunkHead = chunkHead.Substring(0, idx);
                }

                int chunkSize = int.Parse(chunkHead, NumberStyles.HexNumber);

                await WriteLineAsync(chunkHead, cancellationToken);

                if (chunkSize != 0)
                {
                    await CopyBytesFromStream(reader, chunkSize, onCopy, cancellationToken);
                }

                await WriteLineAsync(cancellationToken);

                //chunk trail
                await reader.ReadLineAsync(cancellationToken);

                if (chunkSize == 0)
                {
                    break;
                }
            }
        }

        /// <summary>
        ///     Copies the specified bytes to the stream from the input stream
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="count"></param>
        /// <param name="onCopy"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task CopyBytesFromStream(CustomBinaryReader reader, long count, Action<byte[], int, int> onCopy, CancellationToken cancellationToken)
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

                int bytesRead = await reader.ReadBytesAsync(buffer, bytesToRead, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                remainingBytes -= bytesRead;

                await WriteAsync(buffer, 0, bytesRead, cancellationToken);

                onCopy?.Invoke(buffer, 0, bytesRead);
            }
        }

        /// <summary>
        ///     Writes the request/response headers and body.
        /// </summary>
        /// <param name="requestResponse"></param>
        /// <param name="flush"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        protected async Task WriteAsync(RequestResponseBase requestResponse, bool flush = true, CancellationToken cancellationToken = default)
        {
            var body = requestResponse.CompressBodyAndUpdateContentLength();
            await WriteHeadersAsync(requestResponse.Headers, flush, cancellationToken);

            if (body != null)
            {
                await WriteBodyAsync(body, requestResponse.IsChunked, cancellationToken);
            }
        }
    }
}
