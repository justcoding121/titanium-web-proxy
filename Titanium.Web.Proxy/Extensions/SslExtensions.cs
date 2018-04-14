using StreamExtended;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class SslExtensions
    {
        public static string GetServerName(this ClientHelloInfo clientHelloInfo)
        {
            if (clientHelloInfo.Extensions != null &&
                clientHelloInfo.Extensions.TryGetValue("server_name", out var serverNameExtension))
            {
                return serverNameExtension.Data;
            }

            return null;
        }
    }
}
