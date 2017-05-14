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
        public CertificateEngine Engine
        {
            get { return engine; }
            set
            {
                //For Mono only Bouncy Castle is supported
                if (RunTime.IsRunningOnMono())
                {
                    value = CertificateEngine.BouncyCastle;
                }

                if (value != engine)
                {
                    certEngine = null;
                    engine = value;
                }

                if (certEngine == null)
                {
                    certEngine = engine == CertificateEngine.BouncyCastle ? 
                        (ICertificateMaker) new BCCertificateMaker() 
                        : new WinCertificateMaker();
                }
            }
        }

        private const string defaultRootCertificateIssuer = "Titanium";

        private const string defaultRootRootCertificateName = "Titanium Root Certificate Authority";

        private CertificateEngine engine;

        private ICertificateMaker certEngine;

        private string issuer;

        private string rootCertificateName;

        private bool clearCertificates { get; set; }

        private X509Certificate2 rootCertificate;

        /// <summary>
        /// Cache dictionary
        /// </summary>
        private readonly IDictionary<string, CachedCertificate> certificateCache;

        private readonly Action<Exception> exceptionFunc;

        internal string Issuer
        {
            get { return issuer ?? defaultRootCertificateIssuer; }
            set
            {
                issuer = value;
                ClearRootCertificate();
            }
        }

        internal string RootCertificateName
        {
            get { return rootCertificateName ?? defaultRootRootCertificateName; }
            set
            {
                rootCertificateName = value; 
                ClearRootCertificate();
            }
        }

        internal X509Certificate2 RootCertificate
        {
            get { return rootCertificate; }
            set
            {
                ClearRootCertificate();
                rootCertificate = value;
            }
        }

        /// <summary>
        /// Is the root certificate used by this proxy is valid?
        /// </summary>
        internal bool CertValidated => RootCertificate != null;


        internal CertificateManager(Action<Exception> exceptionFunc)
        {
            this.exceptionFunc = exceptionFunc;
            Engine = CertificateEngine.DefaultWindows;

            certificateCache = new ConcurrentDictionary<string, CachedCertificate>();
        }

        private void ClearRootCertificate()
        {
            certificateCache.Clear();
            rootCertificate = null;
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

        internal X509Certificate2 LoadRootCertificate()
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
            if (RootCertificate == null)
            {
                RootCertificate = LoadRootCertificate();
            }

            if (RootCertificate != null)
            {
                return true;
            }

            try
            {
                RootCertificate = CreateCertificate(RootCertificateName, true);
            }
            catch (Exception e)
            {
                exceptionFunc(e);
            }

            if (RootCertificate != null)
            {
                try
                {
                    var fileName = GetRootCertificatePath();
                    File.WriteAllBytes(fileName, RootCertificate.Export(X509ContentType.Pkcs12));
                }
                catch (Exception e)
                {
                    exceptionFunc(e);
                }
            }

            return RootCertificate != null;
        }

        /// <summary>
        /// Trusts the root certificate.
        /// </summary>
        /// <param name="exceptionFunc"></param>
        internal void TrustRootCertificate(Action<Exception> exceptionFunc)
        {
            //current user
            TrustRootCertificate(StoreLocation.CurrentUser, exceptionFunc);
            
            //current system
            TrustRootCertificate(StoreLocation.LocalMachine, exceptionFunc);
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
                        if (!isRootCertificate && RootCertificate == null)
                        {
                            CreateTrustedRootCertificate();
                        }

                        certificate = certEngine.MakeCertificate(certificateName, isRootCertificate, RootCertificate);
                    }
                    catch (Exception e)
                    {
                        exceptionFunc(e);
                    }
                    if (certificate != null && !certificateCache.ContainsKey(certificateName))
                    {
                        certificateCache.Add(certificateName, new CachedCertificate { Certificate = certificate });
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
            if (RootCertificate == null)
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

                x509RootStore.Add(RootCertificate);
                x509PersonalStore.Add(RootCertificate);
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