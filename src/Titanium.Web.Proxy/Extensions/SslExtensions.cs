using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended;
using Titanium.Web.Proxy.StreamExtended.Models;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class SslExtensions
    {
        internal static readonly List<SslApplicationProtocol> Http11ProtocolAsList =
            new() { SslApplicationProtocol.Http11 };

        internal static readonly List<SslApplicationProtocol> Http2ProtocolAsList =
            new() { SslApplicationProtocol.Http2 };

        internal static string? GetServerName(this ClientHelloInfo clientHelloInfo)
        {
            if (clientHelloInfo.Extensions != null &&
                clientHelloInfo.Extensions.TryGetValue("server_name", out var serverNameExtension))
                return serverNameExtension.Data;

            return null;
        }

        internal static List<SslApplicationProtocol>? GetAlpn(this ClientHelloInfo clientHelloInfo)
        {
            if (clientHelloInfo.Extensions != null && clientHelloInfo.Extensions.TryGetValue("ALPN", out var alpnExtension))
            {
                var alpn = alpnExtension.Alpns;
                if (alpn.Count != 0)
                {
                    return alpn;
                }
            }

            return null;
        }

        internal static List<string>? GetSslProtocols(this ClientHelloInfo clientHelloInfo)
        {
            if (clientHelloInfo.Extensions != null && clientHelloInfo.Extensions.TryGetValue("supported_versions", out var versions))
            {
                var protocols = versions.Protocols;
                if (protocols.Count != 0)
                {
                    return protocols;
                }
            }

            return null;
        }
    }
}
