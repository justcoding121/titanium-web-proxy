using System.IO;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    /// An inteface for http compression
    /// </summary>
    interface ICompression
    {
        Stream GetStream(Stream stream);
    }
}
