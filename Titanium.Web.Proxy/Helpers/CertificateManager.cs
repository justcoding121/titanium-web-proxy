using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;

namespace Titanium.Web.Proxy.Helpers
{
    public class CertificateManager : IDisposable
    {
        private const string CertCreateFormat =
            "-ss {0} -n \"CN={1}, O={2}\" -sky {3} -cy {4} -m 120 -a sha256 -eku 1.3.6.1.5.5.7.3.1 {5}";

        private readonly IDictionary<string, X509Certificate2> _certificateCache;
        private static SemaphoreSlim semaphoreLock = new SemaphoreSlim(1);

        public string Issuer { get; private set; }
        public string RootCertificateName { get; private set; }

        public X509Store MyStore { get; private set; }
        public X509Store RootStore { get; private set; }

        public CertificateManager(string issuer, string rootCertificateName)
        {
            Issuer = issuer;
            RootCertificateName = rootCertificateName;

            MyStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            RootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);

            _certificateCache = new Dictionary<string, X509Certificate2>();
        }

        /// <summary>
        /// Attempts to move a self-signed certificate to the root store.
        /// </summary>
        /// <returns>true if succeeded, else false</returns>
        public async Task<bool> CreateTrustedRootCertificate()
        {
            X509Certificate2 rootCertificate =
              await CreateCertificate(RootStore, RootCertificateName);

            return rootCertificate != null;
        }
        /// <summary>
        /// Attempts to remove the self-signed certificate from the root store.
        /// </summary>
        /// <returns>true if succeeded, else false</returns>
        public async Task<bool> DestroyTrustedRootCertificate()
        {
            return await DestroyCertificate(RootStore, RootCertificateName);
        }

        public X509Certificate2Collection FindCertificates(string certificateSubject)
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

        public async Task<X509Certificate2> CreateCertificate(string certificateName)
        {
            return await CreateCertificate(MyStore, certificateName);
        }
        protected async virtual Task<X509Certificate2> CreateCertificate(X509Store store, string certificateName)
        {

            if (_certificateCache.ContainsKey(certificateName))
                return _certificateCache[certificateName];

            await semaphoreLock.WaitAsync();

            X509Certificate2 certificate = null;
            try
            {
                store.Open(OpenFlags.ReadWrite);
                string certificateSubject = string.Format("CN={0}, O={1}", certificateName, Issuer);

                var certificates =
                    FindCertificates(store, certificateSubject);

                if (certificates != null)
                    certificate = certificates[0];

                if (certificate == null)
                {
                    string[] args = new[] {
                            GetCertificateCreateArgs(store, certificateName) };

                    await CreateCertificate(args);
                    certificates = FindCertificates(store, certificateSubject);

                    return certificates != null ?
                        certificates[0] : null;
                }

                store.Close();
                if (certificate != null && !_certificateCache.ContainsKey(certificateName))
                    _certificateCache.Add(certificateName, certificate);

                return certificate;
            }
            finally
            {
                semaphoreLock.Release();  
            }

        }
        protected virtual Task<int> CreateCertificate(string[] args)
        {

            // there is no non-generic TaskCompletionSource
            var tcs = new TaskCompletionSource<int>();

            var process = new Process();

            string file = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "makecert.exe");

            if (!File.Exists(file))
                throw new Exception("Unable to locate 'makecert.exe'.");

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

        public async Task<bool> DestroyCertificate(string certificateName)
        {
            return await DestroyCertificate(MyStore, certificateName);
        }
        protected virtual async Task<bool> DestroyCertificate(X509Store store, string certificateName)
        {
            await semaphoreLock.WaitAsync();

            X509Certificate2Collection certificates = null;
            try
            {
                store.Open(OpenFlags.ReadWrite);
                string certificateSubject = string.Format("CN={0}, O={1}", certificateName, Issuer);

                certificates = FindCertificates(store, certificateSubject);
                if (certificates != null)
                {
                    store.RemoveRange(certificates);
                    certificates = FindCertificates(store, certificateSubject);
                }
               
                store.Close();
                if (certificates == null &&
                    _certificateCache.ContainsKey(certificateName))
                {
                    _certificateCache.Remove(certificateName);
                }
                return certificates == null;
            }
            finally
            {
                semaphoreLock.Release();    
            }

        }

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

        public void Dispose()
        {
            if (MyStore != null)
                MyStore.Close();

            if (RootStore != null)
                RootStore.Close();
        }
    }
}