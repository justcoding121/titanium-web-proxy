using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Extensions
{
    public static class StreamHelper
    {
        public static async Task CopyToAsync(this Stream input, string initialData, Stream output)
        {
            if (!string.IsNullOrEmpty(initialData))
            {
                var bytes = Encoding.ASCII.GetBytes(initialData);
                await output.WriteAsync(bytes, 0, bytes.Length);
            }
            await input.CopyToAsync(output);
        }

        internal static async Task CopyBytesToStream(this CustomBinaryReader clientStreamReader, Stream stream, long totalBytesToRead)
        {
            var totalbytesRead = 0;

            int bytesToRead;
            if (totalBytesToRead < Constants.BUFFER_SIZE)
            {
                bytesToRead = (int)totalBytesToRead;
            }
            else
                bytesToRead = Constants.BUFFER_SIZE;


            while (totalbytesRead < (int)totalBytesToRead)
            {
                var buffer = await clientStreamReader.ReadBytesAsync(bytesToRead);
                totalbytesRead += buffer.Length;

                var remainingBytes = (int)totalBytesToRead - totalbytesRead;
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
    }
}