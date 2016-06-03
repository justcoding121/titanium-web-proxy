using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class StreamHelper
    {
        internal static async Task CopyToAsync(this Stream input, string initialData, Stream output)
        {
            if (!string.IsNullOrEmpty(initialData))
            {
                var bytes = Encoding.ASCII.GetBytes(initialData);
                await output.WriteAsync(bytes, 0, bytes.Length);
            }
            await input.CopyToAsync(output);
        }

        internal static async Task CopyBytesToStream(this CustomBinaryReader streamReader, Stream stream, long totalBytesToRead)
        {
            var totalbytesRead = 0;

            long bytesToRead;
            if (totalBytesToRead < Constants.BUFFER_SIZE)
            {
                bytesToRead = totalBytesToRead;
            }
            else
                bytesToRead = Constants.BUFFER_SIZE;


            while (totalbytesRead < totalBytesToRead)
            {
                var buffer = await streamReader.ReadBytesAsync(bytesToRead);

                if (buffer.Length == 0)
                    break;

                totalbytesRead += buffer.Length;

                var remainingBytes = totalBytesToRead - totalbytesRead;
                if (remainingBytes < bytesToRead)
                {
                    bytesToRead = remainingBytes;
                }
                await stream.WriteAsync(buffer, 0, buffer.Length);
            }
        }
        internal static async Task CopyBytesToStreamChunked(this CustomBinaryReader clientStreamReader, Stream stream)
        {
            while (true)
            {
                var chuchkHead = await clientStreamReader.ReadLineAsync().ConfigureAwait(false);
                var chunkSize = int.Parse(chuchkHead, NumberStyles.HexNumber);

                if (chunkSize != 0)
                {
                    var buffer = await clientStreamReader.ReadBytesAsync(chunkSize);
                    await stream.WriteAsync(buffer, 0, buffer.Length);
                    //chunk trail
                   await clientStreamReader.ReadLineAsync().ConfigureAwait(false);
                }
                else
                {
                   await clientStreamReader.ReadLineAsync().ConfigureAwait(false);
                    break;
                }
            }
        }
    }
}