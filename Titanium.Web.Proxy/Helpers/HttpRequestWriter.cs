using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Helpers
{
    sealed class HttpRequestWriter : HttpWriter
    {
        public HttpRequestWriter(Stream stream) : base(stream, true)
        {
        }
    }
}
