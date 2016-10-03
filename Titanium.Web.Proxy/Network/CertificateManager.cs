using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Concurrent;
using System.IO;

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

        X509Certificate2 GetRootCertificate()
        {
            var fileName = Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location), "rootCert.pfx");

            if (File.Exists(fileName))
            {
                try
                {
                   return new X509Certificate2(fileName, string.Empty, X509KeyStorageFlags.Exportable);
                   
                }
                catch (Exception e)
                {
                    ProxyServer.ExceptionFunc(e);
                    return null;
                }
            }
            return null;
        }
        /// <summary>
        /// Attempts to create a RootCertificate
        /// </summary>
        /// <returns>true if succeeded, else false</returns>
        internal bool CreateTrustedRootCertificate()
        {

            rootCertificate = GetRootCertificate();
            if (rootCertificate != null)
            {
                return true;
            }
            try
            {
                rootCertificate = CreateCertificate(RootCertificateName, true);
            }
            catch(Exception e)
            {
                ProxyServer.ExceptionFunc(e);
            }
            if (rootCertificate != null)
            {
                try
                {
                    var fileName = Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location), "rootCert.pfx");
                    File.WriteAllBytes(fileName, rootCertificate.Export(X509ContentType.Pkcs12));
                }
                catch(Exception e)
                {
                    ProxyServer.ExceptionFunc(e);
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
                    try
                    {
                        certificate = certEngine.CreateCert(certificateName, isRootCertificate, rootCertificate);
                    }
                    catch(Exception e)
                    {
                        ProxyServer.ExceptionFunc(e);
                    }
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
       
        public bool TrustRootCertificate()
        {
            if (rootCertificate == null)
            {
                return false;
            }
            try
            {
                X509Store x509Store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                x509Store.Open(OpenFlags.ReadWrite);
                try
                {
                    x509Store.Add(rootCertificate);
                }
                finally
                {
                    x509Store.Close();
                }
                return true;
            }
            catch (Exception exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
        }
    }
}