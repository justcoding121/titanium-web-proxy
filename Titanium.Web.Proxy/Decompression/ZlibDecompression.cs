using Ionic.Zlib;
using System.IO;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Decompression
{
    class ZlibDecompression : IDecompression
    {
        public byte[] Decompress(byte[] compressedArray)
        {
            var memoryStream = new MemoryStream(compressedArray);
            using (var decompressor = new ZlibStream(memoryStream, CompressionMode.Decompress))
            {
                var buffer = new byte[Constants.BUFFER_SIZE];

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

