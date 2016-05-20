using Ionic.Zlib;
using System.IO;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Decompression
{
    class ZlibDecompression : IDecompression
    {
        public async Task<byte[]> Decompress(byte[] compressedArray)
        {
            var memoryStream = new MemoryStream(compressedArray);
            using (var decompressor = new ZlibStream(memoryStream, CompressionMode.Decompress))
            {
                var buffer = new byte[Constants.BUFFER_SIZE];

                using (var output = new MemoryStream())
                {
                    int read;
                    while ((read = await decompressor.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                       await output.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    }
                    return output.ToArray();
                }
            }
        }
    }
}

