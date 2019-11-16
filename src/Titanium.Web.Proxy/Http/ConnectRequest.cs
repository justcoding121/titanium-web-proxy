using System;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.StreamExtended;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    /// The tcp tunnel Connect request.
    /// </summary>
    public class ConnectRequest : Request
    {
        public ConnectRequest(string authority)
        {
            Method = "CONNECT";
            Authority = authority;
        }

        public TunnelType TunnelType { get; internal set; }

        public ClientHelloInfo? ClientHelloInfo { get; set; }
    }
}
