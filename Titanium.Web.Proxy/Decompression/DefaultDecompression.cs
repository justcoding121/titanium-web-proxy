using System.IO;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Decompression
{
    /// <summary>
    /// When no compression is specified just return the stream
    /// </summary>
    internal class DefaultDecompression : IDecompression
    {
        public Stream GetStream(Stream stream)
        {
            return stream;
        }
    }
}
