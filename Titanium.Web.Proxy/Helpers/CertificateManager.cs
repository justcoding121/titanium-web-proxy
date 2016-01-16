using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;

namespace Titanium.Web.Proxy.Helpers
{
    public class CertificateManager : IDisposable 
    {
        private const string CertCreateFormat =
            "-ss {0} -n \"CN={1}, O={2}\" -sky {3} -cy {4} -m 120 -a sha256 -eku 1.3.6.1.5.5.7.3.1 {5}";

        private readonly IDictionary<string, X509Certificate2> _certificateCache;

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
        public bool CreateTrustedRootCertificate()
        {
            X509Certificate2 rootCertificate =
                CreateCertificate(RootStore, RootCertificateName);

            return rootCertificate != null;
        }
        /// <summary>
        /// Attempts to remove the self-signed certificate from the root store.
        /// </summary>
        /// <returns>true if succeeded, else false</returns>
        public bool DestroyTrustedRootCertificate()
        {
            return DestroyCertificate(RootStore, RootCertificateName);
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

        public X509Certificate2 CreateCertificate(string certificateName)
        {
            return CreateCertificate(MyStore, certificateName);
        }
        protected virtual X509Certificate2 CreateCertificate(X509Store store, string certificateName)
        {

            if (_certificateCache.ContainsKey(certificateName))
                return _certificateCache[certificateName];

            lock (store)
            {
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

                        CreateCertificate(args);
                        certificates = FindCertificates(store, certificateSubject);

                        return certificates != null ?
                            certificates[0] : null;
                    }
                    return certificate;
                }
                finally
                {
                    store.Close();
                    if (certificate != null && !_certificateCache.ContainsKey(certificateName))
                        _certificateCache.Add(certificateName, certificate);
                }
            }
        }
        protected virtual void CreateCertificate(string[] args)
        {
            using (var process = new Process())
            {
                string file = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "makecert.exe");

                if (!File.Exists(file))
                    throw new Exception("Unable to locate 'makecert.exe'.");

                process.StartInfo.Verb = "runas";
                process.StartInfo.Arguments = args != null ? args[0] : string.Empty;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.FileName = file;

                process.Start();
                process.WaitForExit();
            }
        }

        public bool DestroyCertificate(string certificateName)
        {
            return DestroyCertificate(MyStore, certificateName);
        }
        protected virtual bool DestroyCertificate(X509Store store, string certificateName)
        {
            lock (store)
            {
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
                    return certificates == null;
                }
                finally
                {
                    store.Close();
                    if (certificates == null &&
                        _certificateCache.ContainsKey(certificateName))
                    {
                        _certificateCache.Remove(certificateName);
                    }
                }
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