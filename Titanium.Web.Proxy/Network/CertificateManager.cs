using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Concurrent;
using System.IO;
using Titanium.Web.Proxy.Network.Certificate;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    /// Certificate Engine option
    /// </summary>
    public enum CertificateEngine
    {
        /// <summary>
        /// Uses Windows Certification Generation API
        /// </summary>
        DefaultWindows = 0,

        /// <summary>
        /// Uses BouncyCastle 3rd party library
        /// </summary>
        BouncyCastle = 1
    }

    /// <summary>
    /// A class to manage SSL certificates used by this proxy server
    /// </summary>
    internal class CertificateManager : IDisposable
    {
        private readonly ICertificateMaker certEngine;

        private bool clearCertificates { get; set; }

        /// <summary>
        /// Cache dictionary
        /// </summary>
        private readonly IDictionary<string, CachedCertificate> certificateCache;

        private readonly Action<Exception> exceptionFunc;

        internal string Issuer { get; }
        internal string RootCertificateName { get; }

        internal X509Certificate2 rootCertificate { get; set; }

        internal CertificateManager(CertificateEngine engine,
            string issuer,
            string rootCertificateName,
            Action<Exception> exceptionFunc)
        {
            this.exceptionFunc = exceptionFunc;

            //For Mono only Bouncy Castle is supported
            if (RunTime.IsRunningOnMono() 
                || engine == CertificateEngine.BouncyCastle)
            {
                certEngine = new BCCertificateMaker();
            }
            else
            {
                certEngine = new WinCertificateMaker();
            }

            Issuer = issuer;
            RootCertificateName = rootCertificateName;

            certificateCache = new ConcurrentDictionary<string, CachedCertificate>();
        }

        private string GetRootCertificatePath()
        {
            var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            
            // dynamically loaded assemblies returns string.Empty location
            if (assemblyLocation == string.Empty)
            {
                assemblyLocation = System.Reflection.Assembly.GetEntryAssembly().Location;
            }

            var path = Path.GetDirectoryName(assemblyLocation);
            if (null == path) throw new NullReferenceException();
            var fileName = Path.Combine(path, "rootCert.pfx");
            return fileName;
        }

        internal X509Certificate2 GetRootCertificate()
        {
            var fileName = GetRootCertificatePath();
            if (!File.Exists(fileName)) return null;
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
            catch (Exception e)
            {
                exceptionFunc(e);
            }
            if (rootCertificate != null)
            {
                try
                {
                    var fileName = GetRootCertificatePath();
                    File.WriteAllBytes(fileName, rootCertificate.Export(X509ContentType.Pkcs12));
                }
                catch (Exception e)
                {
                    exceptionFunc(e);
                }
            }
            return rootCertificate != null;
        }

        /// <summary>
        /// Create an SSL certificate
        /// </summary>
        /// <param name="certificateName"></param>
        /// <param name="isRootCertificate"></param>
        /// <returns></returns>
        internal virtual X509Certificate2 CreateCertificate(string certificateName, bool isRootCertificate)
        {

            if (certificateCache.ContainsKey(certificateName))
            {
                var cached = certificateCache[certificateName];
                cached.LastAccess = DateTime.Now;
                return cached.Certificate;
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
                    catch (Exception e)
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
                var cutOff = DateTime.Now.AddMinutes(-1 * certificateCacheTimeOutMinutes);

                var outdated = certificateCache
                    .Where(x => x.Value.LastAccess < cutOff)
                    .ToList();

                foreach (var cache in outdated)
                    certificateCache.Remove(cache.Key);

                //after a minute come back to check for outdated certificates in cache
                await Task.Delay(1000 * 60);
            }
        }

        /// <summary>
        /// Make current machine trust the Root Certificate used by this proxy
        /// </summary>
        /// <param name="storeLocation"></param>
        /// <param name="exceptionFunc"></param>
        /// <returns></returns>
        internal void TrustRootCertificate(StoreLocation storeLocation,
            Action<Exception> exceptionFunc)
        {
            if (rootCertificate == null)
            {
                exceptionFunc(
                    new Exception("Could not set root certificate"
                    + " as system proxy since it is null or empty."));

                return;
            }

            X509Store x509RootStore = new X509Store(StoreName.Root, storeLocation);
            var x509PersonalStore = new X509Store(StoreName.My, storeLocation);

            try
            {
                x509RootStore.Open(OpenFlags.ReadWrite);
                x509PersonalStore.Open(OpenFlags.ReadWrite);

                x509RootStore.Add(rootCertificate);
                x509PersonalStore.Add(rootCertificate);
            }
            catch (Exception e)
            {
                exceptionFunc(
                    new Exception("Failed to make system trust root certificate "
                   + $" for {storeLocation} store location. You may need admin rights.", e));
            }
            finally
            {
                x509RootStore.Close();
                x509PersonalStore.Close();
            }
        }

        public void Dispose()
        {
        }
    }
}