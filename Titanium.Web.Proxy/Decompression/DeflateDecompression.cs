using Ionic.Zlib;
using System.IO;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Decompression
{
    /// <summary>
    /// concrete implementation of deflate de-compression
    /// </summary>
    internal class DeflateDecompression : IDecompression
    {
        public async Task<byte[]> Decompress(byte[] compressedArray, int bufferSize)
        {
            var stream = new MemoryStream(compressedArray);

            using (var decompressor = new DeflateStream(stream, CompressionMode.Decompress))
            {
                var buffer = new byte[bufferSize];

                using (var output = new MemoryStream())
                {
                    int read;
                    while ((read = await decompressor.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                       await output.WriteAsync(buffer, 0, read);
                    }

                    return output.ToArray();
                }
            }
        }
    }
}
