using Ionic.Zlib;
using System.IO;

namespace Titanium.Web.Proxy.Decompression
{
    class DeflateDecompression : IDecompression
    {
        public byte[] Decompress(byte[] compressedArray)
        {
            var stream = new MemoryStream(compressedArray);

            using (var decompressor = new DeflateStream(stream, CompressionMode.Decompress))
            {
                var buffer = new byte[ProxyServer.BUFFER_SIZE];

                using (var output = new MemoryStream())
                {
                    int read;
                    while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                    }

                    return output.ToArray();
                }
            }
        }
    }
}
