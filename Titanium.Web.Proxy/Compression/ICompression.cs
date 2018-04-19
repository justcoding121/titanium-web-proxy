using System.IO;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    ///     An inteface for http compression
    /// </summary>
    internal interface ICompression
    {
        Stream GetStream(Stream stream);
    }
}
