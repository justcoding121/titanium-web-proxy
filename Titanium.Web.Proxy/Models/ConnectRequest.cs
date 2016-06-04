using System;
using System.IO;

namespace Titanium.Web.Proxy.Models
{
    internal class ConnectRequest
    {
        internal Stream Stream { get; set; }
        internal Uri Uri { get; set; }
    }
}
