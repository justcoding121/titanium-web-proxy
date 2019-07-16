using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Shared;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Helpers
{
    internal class HttpWriter : ICustomStreamWriter
    {
        private readonly Stream stream;
        private readonly IBufferPool bufferPool;

        private static readonly byte[] newLine = ProxyConstants.NewLineBytes;

        private static readonly Encoding encoder = Encoding.ASCII;

        internal HttpWriter(Stream stream, IBufferPool bufferPool, int bufferSize)
        {
            BufferSize = bufferSize;
            this.stream = stream;
            this.bufferPool = bufferPool;
        }

        internal int BufferSize { get; }

        /// <summary>
        ///     Writes a line async
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
        /// <returns></returns>
        internal Task WriteLineAsync(CancellationToken cancellationToken = default)
        {
            return WriteAsync(newLine, cancellationToken: cancellationToken);
        }

        internal Task WriteAsync(string value, CancellationToken cancellationToken = default)
        {
            return writeAsyncInternal(value, false, cancellationToken);
        }

        private async Task writeAsyncInternal(string value, bool addNewLine, CancellationToken cancellationToken)
        {
            int newLineChars = addNewLine ? newLine.Length : 0;
            int charCount = value.Length;
            if (charCount < BufferSize - newLineChars)
            {
                var buffer = bufferPool.GetBuffer(BufferSize);
                try
                {
                    int idx = encoder.GetBytes(value, 0, charCount, buffer, 0);
                    if (newLineChars > 0)
                    {
                        Buffer.BlockCopy(newLine, 0, buffer, idx, newLineChars);
                        idx += newLineChars;
                    }

                    await stream.WriteAsync(buffer, 0, idx, cancellationToken);
                }
                finally
                {
                    bufferPool.ReturnBuffer(buffer);
                }
            }
            else
            {
                var buffer = new byte[charCount + newLineChars + 1];
                int idx = encoder.GetBytes(value, 0, charCount, buffer, 0);
                if (newLineChars > 0)
                {
                    Buffer.BlockCopy(newLine, 0, buffer, idx, newLineChars);
                    idx += newLineChars;
                }

                await stream.WriteAsync(buffer, 0, idx, cancellationToken);
            }
        }

        internal Task WriteLineAsync(string value, CancellationToken cancellationToken = default)
        {
            return writeAsyncInternal(value, true, cancellationToken);
        }

        /// <summary>
        ///     Write the headers to client
        /// </summary>
        /// <param name="headers"></param>
        /// <param name="flush"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal async Task WriteHeadersAsync(HeaderCollection headers, bool flush = true,
            CancellationToken cancellationToken = default)
        {
            var headerBuilder = new StringBuilder();
            foreach (var header in headers)
            {
                headerBuilder.Append($"{header.ToString()}{ProxyConstants.NewLine}");
            }
            headerBuilder.Append(ProxyConstants.NewLine);

            await WriteAsync(headerBuilder.ToString(), cancellationToken);

            if (flush)
            {
                await stream.FlushAsync(cancellationToken);
            }
        }

        internal async Task WriteAsync(byte[] data, bool flush = false, CancellationToken cancellationToken = default)
        {
            await stream.WriteAsync(data, 0, data.Length, cancellationToken);
            if (flush)
            {
                await stream.FlushAsync(cancellationToken);
            }
        }

        internal async Task WriteAsync(byte[] data, int offset, int count, bool flush,
            CancellationToken cancellationToken = default)
        {
            await stream.WriteAsync(data, offset, count, cancellationToken);
            if (flush)
            {
                await stream.FlushAsync(cancellationToken);
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
                return writeBodyChunkedAsync(data, cancellationToken);
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
        internal Task CopyBodyAsync(ICustomStreamReader streamReader, bool isChunked, long contentLength,
            Action<byte[], int, int> onCopy, CancellationToken cancellationToken)
        {
            // For chunked request we need to read data as they arrive, until we reach a chunk end symbol
            if (isChunked)
            {
                return copyBodyChunkedAsync(streamReader, onCopy, cancellationToken);
            }

            // http 1.0 or the stream reader limits the stream
            if (contentLength == -1)
            {
                contentLength = long.MaxValue;
            }

            // If not chunked then its easy just read the amount of bytes mentioned in content length header
            return copyBytesFromStream(streamReader, contentLength, onCopy, cancellationToken);
        }

        /// <summary>
        ///     Copies the given input bytes to output stream chunked
        /// </summary>
        /// <param name="data"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task writeBodyChunkedAsync(byte[] data, CancellationToken cancellationToken)
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
        private async Task copyBodyChunkedAsync(ICustomStreamReader reader, Action<byte[], int, int> onCopy,
            CancellationToken cancellationToken)
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
                    await copyBytesFromStream(reader, chunkSize, onCopy, cancellationToken);
                }

                await WriteLineAsync(cancellationToken);

                // chunk trail
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
        private async Task copyBytesFromStream(ICustomStreamReader reader, long count, Action<byte[], int, int> onCopy,
            CancellationToken cancellationToken)
        {
            var buffer = bufferPool.GetBuffer(BufferSize);

            try
            {
                long remainingBytes = count;

                while (remainingBytes > 0)
                {
                    int bytesToRead = buffer.Length;
                    if (remainingBytes < bytesToRead)
                    {
                        bytesToRead = (int)remainingBytes;
                    }

                    int bytesRead = await reader.ReadAsync(buffer, 0, bytesToRead, cancellationToken);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    remainingBytes -= bytesRead;

                    await stream.WriteAsync(buffer, 0, bytesRead, cancellationToken);

                    onCopy?.Invoke(buffer, 0, bytesRead);
                }
            }
            finally
            {
                bufferPool.ReturnBuffer(buffer);
            }
        }

        /// <summary>
        ///     Writes the request/response headers and body.
        /// </summary>
        /// <param name="requestResponse"></param>
        /// <param name="flush"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        protected async Task WriteAsync(RequestResponseBase requestResponse, bool flush = true,
            CancellationToken cancellationToken = default)
        {
            var body = requestResponse.CompressBodyAndUpdateContentLength();
            await WriteHeadersAsync(requestResponse.Headers, flush, cancellationToken);

            if (body != null)
            {
                await WriteBodyAsync(body, requestResponse.IsChunked, cancellationToken);
            }
        }

        /// <summary>When overridden in a derived class, writes a sequence of bytes to the current stream and advances the current position within this stream by the number of bytes written.</summary>
        /// <param name="buffer">An array of bytes. This method copies count bytes from buffer to the current stream.</param>
        /// <param name="offset">The zero-based byte offset in buffer at which to begin copying bytes to the current stream.</param>
        /// <param name="count">The number of bytes to be written to the current stream.</param>
        /// <exception cref="T:System.ArgumentException">The sum of offset and count is greater than the buffer length.</exception>
        /// <exception cref="T:System.ArgumentNullException">buffer is null.</exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">offset or count is negative.</exception>
        /// <exception cref="T:System.IO.IOException">An I/O error occured, such as the specified file cannot be found.</exception>
        /// <exception cref="T:System.NotSupportedException">The stream does not support writing.</exception>
        /// <exception cref="T:System.ObjectDisposedException"><see cref="M:System.IO.Stream.Write(System.Byte[],System.Int32,System.Int32)"></see> was called after the stream was closed.</exception>
        public void Write(byte[] buffer, int offset, int count)
        {
            stream.Write(buffer, offset, count);
        }

        /// <summary>
        ///     Asynchronously writes a sequence of bytes to the current stream, advances the current position within this stream by the number of bytes written, and monitors cancellation requests.
        /// </summary>
        /// <param name="buffer">The buffer to write data from.</param>
        /// <param name="offset">The zero-based byte offset in <paramref name="buffer" /> from which to begin copying bytes to the stream.</param>
        /// <param name="count">The maximum number of bytes to write.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="P:System.Threading.CancellationToken.None" />.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return stream.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }
}
