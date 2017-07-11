using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class DotNetStandardExtensions
    {
#if NET45
        /// <summary>
        /// Disposes the specified client.
        /// Int .NET framework 4.5 the TcpClient class has no Dispose method, 
        /// it is available from .NET 4.6, see
        /// https://msdn.microsoft.com/en-us/library/dn823304(v=vs.110).aspx
        /// </summary>
        /// <param name="client">The client.</param>
        internal static void Dispose(this TcpClient client)
        {
            client.Close();
        }


        /// <summary>
        /// Disposes the specified store.
        /// Int .NET framework 4.5 the X509Store class has no Dispose method, 
        /// it is available from .NET 4.6, see
        /// https://msdn.microsoft.com/en-us/library/system.security.cryptography.x509certificates.x509store.dispose(v=vs.110).aspx
        /// </summary>
        /// <param name="store">The store.</param>
        internal static void Dispose(this X509Store store)
        {
            store.Close();
        }
#endif
    }
}
