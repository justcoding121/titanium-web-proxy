using System.IO;

namespace Titanium.Web.Proxy.Helpers
{
    internal sealed class HttpRequestWriter : HttpWriter
    {
        public HttpRequestWriter(Stream stream, int bufferSize) : base(stream, bufferSize)
        {
        }
    }
}
