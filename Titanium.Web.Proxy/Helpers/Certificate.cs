using System;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using System.Threading;


namespace Titanium.Web.Proxy.Helpers
{
    public class CertificateHelper
    {
        private static X509Store store;
        public static X509Certificate2 GetCertificate(string RootCertificateName, string Hostname)
        {

            if (store == null)
            {
                store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
            }
            var CachedCertificate = GetCertifiateFromStore(RootCertificateName, Hostname);

            if (CachedCertificate == null)
                CreateClientCertificate(RootCertificateName, Hostname);

            return GetCertifiateFromStore(RootCertificateName, Hostname);


        }
        private static X509Certificate2 GetCertifiateFromStore(string RootCertificateName, string Hostname)
        {

            X509Certificate2Collection certificateCollection = store.Certificates.Find(X509FindType.FindBySubjectName, Hostname, true);

            foreach (var certificate in certificateCollection)
            {
                if (certificate.SubjectName.Name.StartsWith("CN=" + Hostname) && certificate.IssuerName.Name.StartsWith("CN=" + RootCertificateName))
                    return certificate;
            }
            return null;
        }

        private static void CreateClientCertificate(string RootCertificateName, string Hostname)
        {

            Process process = new Process();

            // Stop the process from opening a new window
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            // Setup executable and parameters
            process.StartInfo.FileName = @"makecert.exe";
            process.StartInfo.Arguments = @" -pe -ss my -n ""CN=" + Hostname + @", O=DO_NOT_TRUST, OU=Created by http://www.fiddler2.com"" -sky exchange -in " + RootCertificateName + " -is my -eku 1.3.6.1.5.5.7.3.1 -cy end -a sha1 -m 132 -b 06/26/2014";

            // Go
            process.Start();
            process.WaitForExit();


        }

        public static void InstallCertificate(string certificatePath)
        {
            X509Certificate2 certificate = new X509Certificate2(certificatePath);
            X509Store store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);

            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
            store.Close();

            X509Store store1 = new X509Store(StoreName.My, StoreLocation.CurrentUser);

            store1.Open(OpenFlags.ReadWrite);
            store1.Add(certificate);
            store1.Close();
        }

    }
}
