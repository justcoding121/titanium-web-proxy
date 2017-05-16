using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Helpers
{
    /// <summary>
    /// A custom binary reader that would allo us to read string line by line
    /// using the specified encoding
    /// as well as to read bytes as required
    /// </summary>
    internal class CustomBinaryReader : IDisposable
    {
        private readonly CustomBufferedStream stream;
        private readonly int bufferSize;
        private readonly byte[] staticBuffer;
        private readonly Encoding encoding;

        private static readonly ConcurrentQueue<byte[]> buffers
            = new ConcurrentQueue<byte[]>();

        private volatile bool disposed;

        internal CustomBinaryReader(CustomBufferedStream stream, int bufferSize)
        {
            this.stream = stream;
            if (!buffers.TryDequeue(out staticBuffer) || staticBuffer.Length != bufferSize)
            {
                staticBuffer = new byte[bufferSize];
            }

            this.bufferSize = bufferSize;

            //default to UTF-8
            encoding = Encoding.UTF8;
        }

        internal Stream BaseStream => stream;

        /// <summary>
        /// Read a line from the byte stream
        /// </summary>
        /// <returns></returns>
        internal async Task<string> ReadLineAsync()
        {
            var lastChar = default(byte);

            int bufferDataLength = 0;

            // try to use the thread static buffer, usually it is enough
            var buffer = staticBuffer;

            while (stream.DataAvailable || await stream.FillBufferAsync())
            {
                var newChar = stream.ReadByteFromBuffer();
                buffer[bufferDataLength] = newChar;

                //if new line
                if (lastChar == '\r' && newChar == '\n')
                {
                    return encoding.GetString(buffer, 0, bufferDataLength - 1);
                }
                //end of stream
                if (newChar == '\0')
                {
                    return encoding.GetString(buffer, 0, bufferDataLength);
                }

                bufferDataLength++;

                //store last char for new line comparison
                lastChar = newChar;

                if (bufferDataLength == buffer.Length)
                {
                    ResizeBuffer(ref buffer, bufferDataLength * 2);
                }
            }

            return encoding.GetString(buffer, 0, bufferDataLength);
        }

        /// <summary>
        /// Read until the last new line
        /// </summary>
        /// <returns></returns>
        internal async Task<List<string>> ReadAllLinesAsync()
        {
            string tmpLine;
            var requestLines = new List<string>();
            while (!string.IsNullOrEmpty(tmpLine = await ReadLineAsync()))
            {
                requestLines.Add(tmpLine);
            }
            return requestLines;
        }

        /// <summary>
        /// Read until the last new line, ignores the result
        /// </summary>
        /// <returns></returns>
        internal async Task ReadAndIgnoreAllLinesAsync()
        {
            while (!string.IsNullOrEmpty(await ReadLineAsync()))
            {
            }
        }

        /// <summary>
        /// Read the specified number of raw bytes from the base stream
        /// </summary>
        /// <param name="totalBytesToRead"></param>
        /// <returns></returns>
        internal async Task<byte[]> ReadBytesAsync(long totalBytesToRead)
        {
            int bytesToRead = bufferSize;

            var buffer = staticBuffer;
            if (totalBytesToRead < bufferSize)
            {
                bytesToRead = (int)totalBytesToRead;
                buffer = new byte[bytesToRead];
            }

            int bytesRead;
            var totalBytesRead = 0;

            while ((bytesRead = await stream.ReadAsync(buffer, totalBytesRead, bytesToRead)) > 0)
            {
                totalBytesRead += bytesRead;

                if (totalBytesRead == totalBytesToRead)
                    break;

                var remainingBytes = totalBytesToRead - totalBytesRead;
                bytesToRead = Math.Min(bufferSize, (int)remainingBytes);

                if (totalBytesRead + bytesToRead > buffer.Length)
                {
                    ResizeBuffer(ref buffer, Math.Min(totalBytesToRead, buffer.Length * 2));
                }
            }

            if (totalBytesRead != buffer.Length)
            {
                //Normally this should not happen. Resize the buffer anyway
                var newBuffer = new byte[totalBytesRead];
                Buffer.BlockCopy(buffer, 0, newBuffer, 0, totalBytesRead);
                buffer = newBuffer;
            }

            return buffer;
        }

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                buffers.Enqueue(staticBuffer);
            }
        }

        /// <summary>
        /// Increase size of buffer and copy existing content to new buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="size"></param>
        private void ResizeBuffer(ref byte[] buffer, long size)
        {
            var newBuffer = new byte[size];
            Buffer.BlockCopy(buffer, 0, newBuffer, 0, buffer.Length);
            buffer = newBuffer;
        }
    }
}
