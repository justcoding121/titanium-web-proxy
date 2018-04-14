using System;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    ///     An object that holds the cached certificate
    /// </summary>
    internal class CachedCertificate
    {
        internal CachedCertificate()
        {
            LastAccess = DateTime.Now;
        }

        internal X509Certificate2 Certificate { get; set; }

        /// <summary>
        ///     last time this certificate was used
        ///     Usefull in determining its cache lifetime
        /// </summary>
        internal DateTime LastAccess { get; set; }
    }
}
