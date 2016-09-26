using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    /// A class to manage SSL certificates used by this proxy server
    /// </summary>
    internal class CertificateManager : IDisposable
    {
        private CertEnrollEngine certEngine = null;


        /// <summary>
        /// Cache dictionary
        /// </summary>
        private readonly IDictionary<string, CachedCertificate> certificateCache;


        internal string Issuer { get; private set; }
        internal string RootCertificateName { get; private set; }

        internal X509Certificate2 rootCertificate { get; set; }

        internal CertificateManager(string issuer, string rootCertificateName)
        {
            certEngine = new CertEnrollEngine();

            Issuer = issuer;
            RootCertificateName = rootCertificateName;

            certificateCache = new ConcurrentDictionary<string, CachedCertificate>();
        }

        /// <summary>
        /// Attempts to move a self-signed certificate to the root store.
        /// </summary>
        /// <returns>true if succeeded, else false</returns>
        internal bool CreateTrustedRootCertificate()
        {
            if (File.Exists("rootCert.pfx"))
            {
                try
                {
                    rootCertificate = new X509Certificate2("rootCert.pfx", string.Empty, X509KeyStorageFlags.Exportable);
                    if (rootCertificate != null)
                    {
                        return true;
                    }
                }
                catch
                {

                }
            }
            rootCertificate = CreateCertificate(RootCertificateName, true);

            if (rootCertificate != null)
            {
                try
                {
                    File.WriteAllBytes("rootCert.pfx", rootCertificate.Export(X509ContentType.Pkcs12));
                }
                catch
                {

                }
            }
            return rootCertificate != null;
        }
        /// <summary>
        /// Create an SSL certificate
        /// </summary>
        /// <param name="store"></param>
        /// <param name="certificateName"></param>
        /// <param name="isRootCertificate"></param>
        /// <returns></returns>
        public virtual X509Certificate2 CreateCertificate(string certificateName, bool isRootCertificate)
        {
            try
            {
                if (certificateCache.ContainsKey(certificateName))
                {
                    var cached = certificateCache[certificateName];
                    cached.LastAccess = DateTime.Now;
                    return cached.Certificate;
                }
            }
            catch
            {

            }
            X509Certificate2 certificate = null;
            lock (string.Intern(certificateName))
            {
                if (certificateCache.ContainsKey(certificateName) == false)
                {
                    certificate = certEngine.CreateCert(certificateName, isRootCertificate, rootCertificate);
                    if (certificate != null && !certificateCache.ContainsKey(certificateName))
                    {
                        certificateCache.Add(certificateName, new CachedCertificate() { Certificate = certificate });
                    }
                }
                else
                {
                    if (certificateCache.ContainsKey(certificateName))
                    {
                        var cached = certificateCache[certificateName];
                        cached.LastAccess = DateTime.Now;
                        return cached.Certificate;
                    }
                }
            }



            return certificate;

        }


        private bool clearCertificates { get; set; }

        /// <summary>
        /// Stops the certificate cache clear process
        /// </summary>
        internal void StopClearIdleCertificates()
        {
            clearCertificates = false;
        }

        /// <summary>
        /// A method to clear outdated certificates
        /// </summary>
        internal async void ClearIdleCertificates(int certificateCacheTimeOutMinutes)
        {
            clearCertificates = true;
            while (clearCertificates)
            {

                try
                {
                    var cutOff = DateTime.Now.AddMinutes(-1 * certificateCacheTimeOutMinutes);

                    var outdated = certificateCache
                       .Where(x => x.Value.LastAccess < cutOff)
                       .ToList();

                    foreach (var cache in outdated)
                        certificateCache.Remove(cache.Key);
                }
                finally
                {
                }

                //after a minute come back to check for outdated certificates in cache
                await Task.Delay(1000 * 60);
            }
        }

        public void Dispose()
        {
        }
    }
}