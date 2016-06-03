using Ionic.Zlib;
using System.IO;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Decompression
{
    internal class DeflateDecompression : IDecompression
    {
        public async Task<byte[]> Decompress(byte[] compressedArray)
        {
            var stream = new MemoryStream(compressedArray);

            using (var decompressor = new DeflateStream(stream, CompressionMode.Decompress))
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
