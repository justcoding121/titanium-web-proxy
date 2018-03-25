using System.IO;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Decompression
{
    /// <summary>
    /// An interface for decompression
    /// </summary>
    internal interface IDecompression
    {
        Stream GetStream(Stream stream);
    }
}
