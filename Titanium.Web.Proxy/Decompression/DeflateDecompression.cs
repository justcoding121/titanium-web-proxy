using StreamExtended.Helpers;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Decompression
{
    /// <summary>
    /// concrete implementation of deflate de-compression
    /// </summary>
    internal class DeflateDecompression : IDecompression
    {
        public Stream GetStream(Stream stream)
        {
            return new DeflateStream(stream, CompressionMode.Decompress, true);
        }
    }
}
