using Ionic.Zlib;
using System.IO;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    /// concrete implementation of zlib compression
    /// </summary>
   internal class ZlibCompression : ICompression
    {
        public async Task<byte[]> Compress(byte[] responseBody)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZlibStream(ms, CompressionMode.Compress, true))
                {
                   await zip.WriteAsync(responseBody, 0, responseBody.Length);
                }

                return ms.ToArray();
            }
        }
    }
}
