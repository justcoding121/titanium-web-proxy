using System.IO;
using System.IO.Compression;

namespace Titanium.Web.Proxy.Decompression
{
    /// <summary>
    ///     concrete implementation of gzip de-compression
    /// </summary>
    internal class GZipDecompression : IDecompression
    {
        public Stream GetStream(Stream stream)
        {
            return new GZipStream(stream, CompressionMode.Decompress, true);
        }
    }
}
