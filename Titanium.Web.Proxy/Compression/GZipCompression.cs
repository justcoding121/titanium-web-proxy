using Ionic.Zlib;
using System.IO;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Compression
{
    class GZipCompression : ICompression
    {
        public async Task<byte[]> Compress(byte[] responseBody)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new GZipStream(ms, CompressionMode.Compress, true))
                {
                   await zip.WriteAsync(responseBody, 0, responseBody.Length);
                }

                return ms.ToArray();
            }
        }
    }
}
