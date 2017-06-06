using System;
using System.Collections.Generic;
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
        private readonly Encoding encoding;

        private bool disposed;

        internal byte[] Buffer { get; }

        internal CustomBinaryReader(CustomBufferedStream stream, int bufferSize)
        {
            this.stream = stream;
            Buffer = BufferPool.GetBuffer(bufferSize);

            //default to UTF-8
            encoding = Encoding.UTF8;
        }

        /// <summary>
        /// Read a line from the byte stream
        /// </summary>
        /// <returns></returns>
        internal async Task<string> ReadLineAsync()
        {
            var lastChar = default(byte);

            int bufferDataLength = 0;

            // try to use the thread static buffer, usually it is enough
            var buffer = Buffer;

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

            if (bufferDataLength == 0)
            {
                return null;
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
        /// Read the specified number (or less) of raw bytes from the base stream to the given buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="bytesToRead"></param>
        /// <returns>The number of bytes read</returns>
        internal Task<int> ReadBytesAsync(byte[] buffer, int bytesToRead)
        {
            return stream.ReadAsync(buffer, 0, bytesToRead);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                BufferPool.ReturnBuffer(Buffer);
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
            System.Buffer.BlockCopy(buffer, 0, newBuffer, 0, buffer.Length);
            buffer = newBuffer;
        }
    }
}
