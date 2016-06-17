using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Helpers
{

    /// <summary>
    /// A custom binary reader that would allo us to read string line by line
    /// using the specified encoding
    /// as well as to read bytes as required
    /// </summary>
    internal class CustomBinaryReader : IDisposable
    {
        private Stream stream;
        private Encoding encoding;

        internal CustomBinaryReader(Stream stream)
        {
            this.stream = stream;

            //default to UTF-8
            this.encoding = Encoding.UTF8;
        }

        internal Stream BaseStream => stream;

        /// <summary>
        /// Read a line from the byte stream
        /// </summary>
        /// <returns></returns>
        internal async Task<string> ReadLineAsync()
        {
            using (var readBuffer = new MemoryStream())
            {
                try
                {
                    var lastChar = default(char);
                    var buffer = new byte[1];

                    while ((await this.stream.ReadAsync(buffer, 0, 1)) > 0)
                    {
                        //if new line
                        if (lastChar == '\r' && buffer[0] == '\n')
                        {
                            var result = readBuffer.ToArray();
                            return  encoding.GetString(result.SubArray(0, result.Length - 1));
                        }
                        //end of stream
                        if (buffer[0] == '\0')
                        {
                            return encoding.GetString(readBuffer.ToArray());
                        }

                        await readBuffer.WriteAsync(buffer,0,1);

                        //store last char for new line comparison
                        lastChar = (char)buffer[0];
                    }

                    return encoding.GetString(readBuffer.ToArray());
                }
                catch (IOException)
                {
                    throw;
                }
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
            while (!string.IsNullOrEmpty(tmpLine = await ReadLineAsync()))
            {
                requestLines.Add(tmpLine);
            }
            return requestLines;
        }

        /// <summary>
        /// Read the specified number of raw bytes from the base stream
        /// </summary>
        /// <param name="totalBytesToRead"></param>
        /// <returns></returns>
        internal async Task<byte[]> ReadBytesAsync(int bufferSize, long totalBytesToRead)
        {
            int bytesToRead = bufferSize;

            if (totalBytesToRead < bufferSize)
                bytesToRead = (int)totalBytesToRead;

            var buffer = new byte[bufferSize];

            var bytesRead = 0;
            var totalBytesRead = 0;

            using (var outStream = new MemoryStream())
            {
                while ((bytesRead += await this.stream.ReadAsync(buffer, 0, bytesToRead)) > 0)
                {
                    await outStream.WriteAsync(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;

                    if (totalBytesRead == totalBytesToRead)
                        break;

                    bytesRead = 0;
                    var remainingBytes = (totalBytesToRead - totalBytesRead);
                    bytesToRead = remainingBytes > (long)bufferSize ? bufferSize : (int)remainingBytes;
                }

                return outStream.ToArray();
            }

        }

        public void Dispose()
        {

        }
    }
}