using Ionic.Zlib;
using System.IO;

namespace Titanium.Web.Proxy.Compression
{
    class GZipCompression : ICompression
    {
        public byte[] Compress(byte[] responseBody)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new GZipStream(ms, CompressionMode.Compress, true))
                {
                    zip.Write(responseBody, 0, responseBody.Length);
                }

                return ms.ToArray();
            }
        }
    }
}
