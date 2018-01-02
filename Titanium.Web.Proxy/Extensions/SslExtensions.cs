using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StreamExtended;

namespace Titanium.Web.Proxy.Extensions
{
    static class SslExtensions
    {
        public static string GetServerName(this ClientHelloInfo clientHelloInfo)
        {
            if (clientHelloInfo.Extensions != null && clientHelloInfo.Extensions.TryGetValue("server_name", out var serverNameExtension))
            {
                return serverNameExtension.Data;
            }

            return null;
        }
    }
}
