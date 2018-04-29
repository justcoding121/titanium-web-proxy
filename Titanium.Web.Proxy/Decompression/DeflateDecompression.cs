using System.IO;
using System.IO.Compression;

namespace Titanium.Web.Proxy.Decompression
{
    /// <summary>
    ///     concrete implementation of deflate de-compression
    /// </summary>
    internal class DeflateDecompression : IDecompression
    {
        public Stream GetStream(Stream stream)
        {
            return new DeflateStream(stream, CompressionMode.Decompress, true);
        }
    }
}
