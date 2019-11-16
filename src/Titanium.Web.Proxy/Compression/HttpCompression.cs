using System.IO.Compression;

namespace Titanium.Web.Proxy.Compression
{
    internal enum HttpCompression
    {
        Unsupported,
        Gzip,
        Deflate,
        Brotli,
    }
}
