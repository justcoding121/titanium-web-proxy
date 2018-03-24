using StreamExtended.Helpers;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Decompression
{
    /// <summary>
    /// concrete implementation of gzip de-compression
    /// </summary>
    internal class GZipDecompression : IDecompression
    {
        public Stream GetStream(Stream stream)
        {
            return new GZipStream(stream, CompressionMode.Decompress);
        }
    }
}
