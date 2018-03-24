using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    /// Concrete implementation of deflate compression
    /// </summary>
    internal class DeflateCompression : ICompression
    {
        public async Task<byte[]> Compress(byte[] body)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new DeflateStream(ms, CompressionMode.Compress, true))
                {
                    await zip.WriteAsync(body, 0, body.Length);
                }

                return ms.ToArray();
            }
        }
    }
}
