using System.IO;

namespace Titanium.Web.Proxy.Helpers
{
    sealed class HttpRequestWriter : HttpWriter
    {
        public HttpRequestWriter(Stream stream, int bufferSize)
            : base(stream, bufferSize, true)
        {
        }
    }
}
