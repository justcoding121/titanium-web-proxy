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
                output.Write(bytes, 0, bytes.Length);
            }
            await input.CopyToAsync(output);
        }

        internal static void CopyBytesToStream(this CustomBinaryReader clientStreamReader, Stream stream, long totalBytesToRead)
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
                var buffer = clientStreamReader.ReadBytes(bytesToRead);
                totalbytesRead += buffer.Length;

                var remainingBytes = (int)totalBytesToRead - totalbytesRead;
                if (remainingBytes < bytesToRead)
                {
                    bytesToRead = remainingBytes;
                }
                stream.Write(buffer, 0, buffer.Length);
            }
        }
        internal static void CopyBytesToStreamChunked(this CustomBinaryReader clientStreamReader, Stream stream)
        {
            while (true)
            {
                var chuchkHead = clientStreamReader.ReadLine();
                var chunkSize = int.Parse(chuchkHead, NumberStyles.HexNumber);

                if (chunkSize != 0)
                {
                    var buffer = clientStreamReader.ReadBytes(chunkSize);
                    stream.Write(buffer, 0, buffer.Length);
                    //chunk trail
                    clientStreamReader.ReadLine();
                }
                else
                {
                    clientStreamReader.ReadLine();
                    break;
                }
            }
        }
    }
}