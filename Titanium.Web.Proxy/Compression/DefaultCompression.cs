using System.IO;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    /// When no compression is specified just return the stream
    /// </summary>
    internal class DefaultCompression : ICompression
    {
        public Stream GetStream(Stream stream)
        {
            return stream;
        }
    }
}
