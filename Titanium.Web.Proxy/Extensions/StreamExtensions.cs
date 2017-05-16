using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Extensions
{
    /// <summary>
    /// Extensions used for Stream and CustomBinaryReader objects
    /// </summary>
    internal static class StreamExtensions
    {
        /// <summary>
        /// Copy streams asynchronously with an initial data inserted to the beginning of stream
        /// </summary>
        /// <param name="input"></param>
        /// <param name="initialData"></param>
        /// <param name="output"></param>
        /// <returns></returns>
        internal static async Task CopyToAsync(this Stream input, string initialData, Stream output)
        {
            if (!string.IsNullOrEmpty(initialData))
            {
                var bytes = Encoding.ASCII.GetBytes(initialData);
                await output.WriteAsync(bytes, 0, bytes.Length);
            }

            await input.CopyToAsync(output);
        }

        /// <summary>
        /// copies the specified bytes to the stream from the input stream
        /// </summary>
        /// <param name="streamReader"></param>
        /// <param name="bufferSize"></param>
        /// <param name="stream"></param>
        /// <param name="totalBytesToRead"></param>
        /// <returns></returns>
        internal static async Task CopyBytesToStream(this CustomBinaryReader streamReader, int bufferSize, Stream stream, long totalBytesToRead)
        {
            var totalbytesRead = 0;

            long bytesToRead = totalBytesToRead < bufferSize ? totalBytesToRead : bufferSize;

            while (totalbytesRead < totalBytesToRead)
            {
                var buffer = await streamReader.ReadBytesAsync(bytesToRead);

                if (buffer.Length == 0)
                {
                    break;
                }

                totalbytesRead += buffer.Length;

                var remainingBytes = totalBytesToRead - totalbytesRead;
                if (remainingBytes < bytesToRead)
                {
                    bytesToRead = remainingBytes;
                }

                await stream.WriteAsync(buffer, 0, buffer.Length);
            }
        }

        /// <summary>
        /// Copies the stream chunked
        /// </summary>
        /// <param name="clientStreamReader"></param>
        /// <param name="stream"></param>
        /// <returns></returns>
        internal static async Task CopyBytesToStreamChunked(this CustomBinaryReader clientStreamReader, Stream stream)
        {
            while (true)
            {
                var chuchkHead = await clientStreamReader.ReadLineAsync();
                var chunkSize = int.Parse(chuchkHead, NumberStyles.HexNumber);

                if (chunkSize != 0)
                {
                    var buffer = await clientStreamReader.ReadBytesAsync(chunkSize);
                    await stream.WriteAsync(buffer, 0, buffer.Length);
                    //chunk trail
                    await clientStreamReader.ReadLineAsync();
                }
                else
                {
                    await clientStreamReader.ReadLineAsync();
                    break;
                }
            }
        }

        /// <summary>
        /// Writes the byte array body to the given stream; optionally chunked
        /// </summary>
        /// <param name="clientStream"></param>
        /// <param name="data"></param>
        /// <param name="isChunked"></param>
        /// <returns></returns>
        internal static async Task WriteResponseBody(this Stream clientStream, byte[] data, bool isChunked)
        {
            if (!isChunked)
            {
                await clientStream.WriteAsync(data, 0, data.Length);
            }
            else
            {
                await WriteResponseBodyChunked(data, clientStream);
            }
        }

        /// <summary>
        /// Copies the specified content length number of bytes to the output stream from the given inputs stream
        /// optionally chunked
        /// </summary>
        /// <param name="inStreamReader"></param>
        /// <param name="bufferSize"></param>
        /// <param name="outStream"></param>
        /// <param name="isChunked"></param>
        /// <param name="contentLength"></param>
        /// <returns></returns>
        internal static async Task WriteResponseBody(this CustomBinaryReader inStreamReader, int bufferSize, Stream outStream, bool isChunked, long contentLength)
        {
            if (!isChunked)
            {
                //http 1.0
                if (contentLength == -1)
                {
                    contentLength = long.MaxValue;
                }

                int bytesToRead = bufferSize;

                if (contentLength < bufferSize)
                {
                    bytesToRead = (int)contentLength;
                }

                var buffer = new byte[bufferSize];

                var bytesRead = 0;
                var totalBytesRead = 0;

                while ((bytesRead += await inStreamReader.BaseStream.ReadAsync(buffer, 0, bytesToRead)) > 0)
                {
                    await outStream.WriteAsync(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;

                    if (totalBytesRead == contentLength)
                        break;

                    bytesRead = 0;
                    var remainingBytes = contentLength - totalBytesRead;
                    bytesToRead = remainingBytes > (long)bufferSize ? bufferSize : (int)remainingBytes;
                }
            }
            else
            {
                await WriteResponseBodyChunked(inStreamReader, outStream);
            }
        }

        /// <summary>
        /// Copies the streams chunked
        /// </summary>
        /// <param name="inStreamReader"></param>
        /// <param name="outStream"></param>
        /// <returns></returns>
        internal static async Task WriteResponseBodyChunked(this CustomBinaryReader inStreamReader, Stream outStream)
        {
            while (true)
            {
                var chunkHead = await inStreamReader.ReadLineAsync();
                var chunkSize = int.Parse(chunkHead, NumberStyles.HexNumber);

                if (chunkSize != 0)
                {
                    var buffer = await inStreamReader.ReadBytesAsync(chunkSize);

                    var chunkHeadBytes = Encoding.ASCII.GetBytes(chunkSize.ToString("x2"));

                    await outStream.WriteAsync(chunkHeadBytes, 0, chunkHeadBytes.Length);
                    await outStream.WriteAsync(ProxyConstants.NewLineBytes, 0, ProxyConstants.NewLineBytes.Length);

                    await outStream.WriteAsync(buffer, 0, chunkSize);
                    await outStream.WriteAsync(ProxyConstants.NewLineBytes, 0, ProxyConstants.NewLineBytes.Length);

                    await inStreamReader.ReadLineAsync();
                }
                else
                {
                    await inStreamReader.ReadLineAsync();
                    await outStream.WriteAsync(ProxyConstants.ChunkEnd, 0, ProxyConstants.ChunkEnd.Length);
                    break;
                }
            }
        }

        /// <summary>
        /// Copies the given input bytes to output stream chunked
        /// </summary>
        /// <param name="data"></param>
        /// <param name="outStream"></param>
        /// <returns></returns>
        internal static async Task WriteResponseBodyChunked(this byte[] data, Stream outStream)
        {
            var chunkHead = Encoding.ASCII.GetBytes(data.Length.ToString("x2"));

            await outStream.WriteAsync(chunkHead, 0, chunkHead.Length);
            await outStream.WriteAsync(ProxyConstants.NewLineBytes, 0, ProxyConstants.NewLineBytes.Length);
            await outStream.WriteAsync(data, 0, data.Length);
            await outStream.WriteAsync(ProxyConstants.NewLineBytes, 0, ProxyConstants.NewLineBytes.Length);

            await outStream.WriteAsync(ProxyConstants.ChunkEnd, 0, ProxyConstants.ChunkEnd.Length);
        }
    }
}
