using System.IO;
using System.IO.Compression;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Decompression
{
    class GZipDecompression : IDecompression
    {
        public byte[] Decompress(byte[] compressedArray)
        {
            using (var decompressor = new GZipStream(new MemoryStream(compressedArray), CompressionMode.Decompress))
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
