using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Helpers
{
    /// <summary>
    /// A custom binary reader that would allo us to read string line by line
    /// using the specified encoding
    /// as well as to read bytes as required
    /// </summary>
    public class CustomBinaryReader : IDisposable
    {
        private Stream stream;


        internal CustomBinaryReader(Stream stream)
        {
            this.stream = stream;
        }


        public Stream BaseStream => stream;

        /// <summary>
        /// Read a line from the byte stream
        /// </summary>
        /// <returns></returns>
        internal async Task<string> ReadLineAsync()
        {
            var readBuffer = new StringBuilder();

            try
            {
                var lastChar = default(char);
                var buffer = new byte[1];

                while (await this.stream.ReadAsync(buffer, 0, 1).ConfigureAwait(false) > 0)
                {
                    if (lastChar == '\r' && buffer[0] == '\n')
                    {
                        return readBuffer.Remove(readBuffer.Length - 1, 1).ToString();
                    }
                    if (buffer[0] == '\0')
                    {
                        return readBuffer.ToString();
                    }
                    readBuffer.Append((char)buffer[0]);
                    lastChar = (char)buffer[0];
                }

                return readBuffer.ToString();
            }
            catch (IOException)
            {
                return readBuffer.ToString();
            }
        }

        /// <summary>
        /// Read until the last new line
        /// </summary>
        /// <returns></returns>
        internal async Task<List<string>> ReadAllLinesAsync()
        {
            string tmpLine;
            var requestLines = new List<string>();
            while (!string.IsNullOrEmpty(tmpLine = await ReadLineAsync().ConfigureAwait(false)))
            {
                requestLines.Add(tmpLine);
            }
            return requestLines;
        }

        internal async Task<byte[]> ReadBytesAsync(long totalBytesToRead)
        {
            int bytesToRead = Constants.BUFFER_SIZE;

            if (totalBytesToRead < Constants.BUFFER_SIZE)
                bytesToRead = (int)totalBytesToRead;

            var buffer = new byte[Constants.BUFFER_SIZE];

            var bytesRead = 0;
            var totalBytesRead = 0;

            using (var outStream = new MemoryStream())
            {
                while ((bytesRead += await this.stream.ReadAsync(buffer, 0, bytesToRead).ConfigureAwait(false)) > 0)
                {
                    await outStream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                    totalBytesRead += bytesRead;

                    if (totalBytesRead == totalBytesToRead)
                        break;

                    bytesRead = 0;
                    var remainingBytes = (totalBytesToRead - totalBytesRead);
                    bytesToRead = remainingBytes > (long)Constants.BUFFER_SIZE ? Constants.BUFFER_SIZE : (int)remainingBytes;
                }

                return outStream.ToArray();
            }

        }

        public void Dispose()
        {

        }
    }
}