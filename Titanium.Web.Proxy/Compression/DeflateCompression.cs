using System.IO;
using System.IO.Compression;

namespace Titanium.Web.Proxy.Compression
{
    class DeflateCompression : ICompression
    {
        public byte[] Compress(byte[] responseBody)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new DeflateStream(ms, CompressionMode.Compress, true))
                {
                    zip.Write(responseBody, 0, responseBody.Length);
                }

                return ms.ToArray();
            }
        }
    }
}
