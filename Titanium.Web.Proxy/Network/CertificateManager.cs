using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    /// A class to manage SSL certificates used by this proxy server
    /// </summary>
    internal class CertificateManager : IDisposable
    {
        private const string CertCreateFormat =
            "-ss {0} -n \"CN={1}, O={2}\" -sky {3} -cy {4} -m 120 -a sha256 -eku 1.3.6.1.5.5.7.3.1 {5}";

        /// <summary>
        /// Cache dictionary
        /// </summary>
        private readonly IDictionary<string, CachedCertificate> certificateCache;

        /// <summary>
        /// A lock to manage concurrency
        /// </summary>
        private SemaphoreSlim semaphoreLock = new SemaphoreSlim(1);

        internal string Issuer { get; private set; }
        internal string RootCertificateName { get; private set; }

        internal X509Store MyStore { get; private set; }
        internal X509Store RootStore { get; private set; }

        internal CertificateManager(string issuer, string rootCertificateName)
        {
            Issuer = issuer;
            RootCertificateName = rootCertificateName;

            MyStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            RootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);

            certificateCache = new Dictionary<string, CachedCertificate>();
        }

        /// <summary>
        /// Attempts to move a self-signed certificate to the root store.
        /// </summary>
        /// <returns>true if succeeded, else false</returns>
        internal async Task<bool> CreateTrustedRootCertificate()
        {
            X509Certificate2 rootCertificate =
              await CreateCertificate(RootStore, RootCertificateName, true);

            return rootCertificate != null;
        }
        /// <summary>
        /// Attempts to remove the self-signed certificate from the root store.
        /// </summary>
        /// <returns>true if succeeded, else false</returns>
        internal bool DestroyTrustedRootCertificate()
        {
            return DestroyCertificate(RootStore, RootCertificateName, false);
        }

        internal X509Certificate2Collection FindCertificates(string certificateSubject)
        {
            return FindCertificates(MyStore, certificateSubject);
        }
        protected virtual X509Certificate2Collection FindCertificates(X509Store store, string certificateSubject)
        {
            X509Certificate2Collection discoveredCertificates = store.Certificates
                .Find(X509FindType.FindBySubjectDistinguishedName, certificateSubject, false);

            return discoveredCertificates.Count > 0 ?
                discoveredCertificates : null;
        }

        internal async Task<X509Certificate2> CreateCertificate(string certificateName, bool isRootCertificate)
        {
            return await CreateCertificate(MyStore, certificateName, isRootCertificate);
        }

        /// <summary>
        /// Create an SSL certificate
        /// </summary>
        /// <param name="store"></param>
        /// <param name="certificateName"></param>
        /// <param name="isRootCertificate"></param>
        /// <returns></returns>
        protected async virtual Task<X509Certificate2> CreateCertificate(X509Store store, string certificateName, bool isRootCertificate)
        {
            await semaphoreLock.WaitAsync();

            try
            {
                if (certificateCache.ContainsKey(certificateName))
                {
                    var cached = certificateCache[certificateName];
                    cached.LastAccess = DateTime.Now;
                    return cached.Certificate;
                }

                X509Certificate2 certificate = null;
                store.Open(OpenFlags.ReadWrite);
                string certificateSubject = string.Format("CN={0}, O={1}", certificateName, Issuer);

                X509Certificate2Collection certificates;

                if (isRootCertificate)
                {
                    certificates = FindCertificates(store, certificateSubject);

                    if (certificates != null)
                    {
                        certificate = certificates[0];
                    }
                }

                if (certificate == null)
                {
                    string[] args = new[] {
                            GetCertificateCreateArgs(store, certificateName) };

                    await CreateCertificate(args);
                    certificates = FindCertificates(store, certificateSubject);

                    //remove it from store
                    if (!isRootCertificate)
                    {
                        DestroyCertificate(certificateName);
                    }

                    if (certificates != null)
                    {
                        certificate = certificates[0];
                    }
                }

                store.Close();

                if (certificate != null && !certificateCache.ContainsKey(certificateName))
                {
                    certificateCache.Add(certificateName, new CachedCertificate() { Certificate = certificate });
                }

                return certificate;
            }
            finally
            {
                semaphoreLock.Release();
            }
        }

        /// <summary>
        /// Create certificate using makecert.exe
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        protected virtual Task<int> CreateCertificate(string[] args)
        {

            // there is no non-generic TaskCompletionSource
            var tcs = new TaskCompletionSource<int>();

            var process = new Process();

            string file = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "makecert.exe");

            if (!File.Exists(file))
            {
                throw new Exception("Unable to locate 'makecert.exe'.");
            }

            process.StartInfo.Verb = "runas";
            process.StartInfo.Arguments = args != null ? args[0] : string.Empty;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.FileName = file;
            process.EnableRaisingEvents = true;

            process.Exited += (sender, processArgs) =>
            {
                tcs.SetResult(process.ExitCode);
                process.Dispose();
            };

            bool started = process.Start();

            if (!started)
            {
                //you may allow for the process to be re-used (started = false) 
                //but I'm not sure about the guarantees of the Exited event in such a case
                throw new InvalidOperationException("Could not start process: " + process);
            }

            return tcs.Task;

        }
        /// <summary>
        /// Destroy an SSL certificate from local store
        /// </summary>
        /// <param name="certificateName"></param>
        /// <returns></returns>
        internal bool DestroyCertificate(string certificateName)
        {
            return DestroyCertificate(MyStore, certificateName, false);
        }

        /// <summary>
        /// Destroy certificate from the specified store
        /// optionally also remove from proxy certificate cache
        /// </summary>
        /// <param name="store"></param>
        /// <param name="certificateName"></param>
        /// <param name="removeFromCache"></param>
        /// <returns></returns>
        protected virtual bool DestroyCertificate(X509Store store, string certificateName, bool removeFromCache)
        {
            X509Certificate2Collection certificates = null;

            store.Open(OpenFlags.ReadWrite);
            string certificateSubject = string.Format("CN={0}, O={1}", certificateName, Issuer);

            certificates = FindCertificates(store, certificateSubject);

            if (certificates != null)
            {
                store.RemoveRange(certificates);
                certificates = FindCertificates(store, certificateSubject);
            }

            store.Close();
            if (removeFromCache &&
                certificateCache.ContainsKey(certificateName))
            {
                certificateCache.Remove(certificateName);
            }

            return certificates == null;
        }
        /// <summary>
        /// Create the neccessary arguments for makecert.exe to create the required certificate
        /// </summary>
        /// <param name="store"></param>
        /// <param name="certificateName"></param>
        /// <returns></returns>
        protected virtual string GetCertificateCreateArgs(X509Store store, string certificateName)
        {
            bool isRootCertificate =
                (certificateName == RootCertificateName);

            string certCreatArgs = string.Format(CertCreateFormat,
                store.Name, certificateName, Issuer,
                isRootCertificate ? "signature" : "exchange",
                isRootCertificate ? "authority" : "end",
                isRootCertificate ? "-h 1 -r" : string.Format("-pe -in \"{0}\" -is Root", RootCertificateName));

            return certCreatArgs;
        }

        private  bool clearCertificates { get; set; }

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
                await semaphoreLock.WaitAsync();

                try
                {
                    var cutOff = DateTime.Now.AddMinutes(-1 * certificateCacheTimeOutMinutes);

                    var outdated = certificateCache
                       .Where(x => x.Value.LastAccess < cutOff)
                       .ToList();

                    foreach (var cache in outdated)
                        certificateCache.Remove(cache.Key);
                }
                finally {
                    semaphoreLock.Release();
                }

                //after a minute come back to check for outdated certificates in cache
                await Task.Delay(1000 * 60);
            }
        }

        public void Dispose()
        {
            if (MyStore != null)
            {
                MyStore.Close();
            }

            if (RootStore != null)
            {
                RootStore.Close();
            }
        }
    }
}