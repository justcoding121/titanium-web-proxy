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

        /// <summary>
        ///     Safe browser-facing ALPN when HTTP/2 should be offered: always include HTTP/1.1 so a
        ///     mismatched or partially-parsed ClientHello cannot fail AuthenticateAsServer with
        ///     SEC_E_NO_APPLICATION_PROTOCOL (0x80090367).
        /// </summary>
        internal static readonly List<SslApplicationProtocol> Http2AndHttp11ProtocolAsList =
            new() { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 };

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
