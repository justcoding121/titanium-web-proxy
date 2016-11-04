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
        private CertificateMaker certEngine = null;

        private bool clearCertificates { get; set; }
        /// <summary>
        /// Cache dictionary
        /// </summary>
        private readonly IDictionary<string, CachedCertificate> certificateCache;

        private Action<Exception> exceptionFunc;

        internal string Issuer { get; private set; }
        internal string RootCertificateName { get; private set; }

        internal X509Certificate2 rootCertificate { get; set; }

        internal CertificateManager(string issuer, string rootCertificateName, Action<Exception> exceptionFunc)
        {
            this.exceptionFunc = exceptionFunc;

            certEngine = new CertificateMaker();

            Issuer = issuer;
            RootCertificateName = rootCertificateName;      

            certificateCache = new ConcurrentDictionary<string, CachedCertificate>();
        }

        internal X509Certificate2 GetRootCertificate()
        {
            var fileName = Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "rootCert.pfx");

            if (File.Exists(fileName))
            {
                try
                {
                   return new X509Certificate2(fileName, string.Empty, X509KeyStorageFlags.Exportable);
                   
                }
                catch (Exception e)
                {
                    exceptionFunc(e);
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
                exceptionFunc(e);
            }
            if (rootCertificate != null)
            {
                try
                {
                    var fileName = Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "rootCert.pfx");
                    File.WriteAllBytes(fileName, rootCertificate.Export(X509ContentType.Pkcs12));
                }
                catch(Exception e)
                {
                    exceptionFunc(e);
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
        internal virtual X509Certificate2 CreateCertificate(string certificateName, bool isRootCertificate)
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
                        certificate = certEngine.MakeCertificate(certificateName, isRootCertificate, rootCertificate);
                    }
                    catch(Exception e)
                    {
                        exceptionFunc(e);
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
       
        internal bool TrustRootCertificate()
        {
            if (rootCertificate == null)
            {
                return false;
            }
            try
            {
                X509Store x509RootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                var x509PersonalStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);

                x509RootStore.Open(OpenFlags.ReadWrite);
                x509PersonalStore.Open(OpenFlags.ReadWrite);

                try
                {
                    x509RootStore.Add(rootCertificate);
                    x509PersonalStore.Add(rootCertificate);
                }
                finally
                {
                    x509RootStore.Close();
                    x509PersonalStore.Close();
                }
                return true;
            }
            catch 
            {
                return false;
            }
        }

        public void Dispose()
        {
        }
    }
}