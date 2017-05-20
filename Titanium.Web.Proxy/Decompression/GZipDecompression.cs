using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Decompression
{
    /// <summary>
    /// concrete implementation of gzip de-compression
    /// </summary>
    internal class GZipDecompression : IDecompression
    {
        public async Task<byte[]> Decompress(byte[] compressedArray, int bufferSize)
        {
            using (var decompressor = new GZipStream(new MemoryStream(compressedArray), CompressionMode.Decompress))
            {
                var buffer = BufferPool.GetBuffer(bufferSize);
                try
                {
                    using (var output = new MemoryStream())
                    {
                        int read;
                        while ((read = await decompressor.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, read);
                        }

                        return output.ToArray();
                    }
                }
                finally
                {
                    BufferPool.ReturnBuffer(buffer);
                }
            }
        }
    }
}
