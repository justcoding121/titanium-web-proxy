using System.IO;

namespace Titanium.Web.Proxy.Helpers
{
    sealed class HttpRequestWriter : HttpWriter
    {
        public HttpRequestWriter(Stream stream) : base(stream, true)
        {
        }
    }
}
