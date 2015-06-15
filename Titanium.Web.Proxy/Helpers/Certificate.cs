using System;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using System.Threading;

namespace Titanium.Web.Proxy.Helpers
{
    public class CertificateHelper
    {
        private static X509Store store;
        public static X509Certificate2 GetCertificate(string Hostname)
        {

            if (store == null)
            {
                store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
            }
            var CachedCertificate = GetCertifiateFromStore(Hostname);

            if (CachedCertificate == null)
                CreateClientCertificate(Hostname);

            return GetCertifiateFromStore(Hostname);


        }
        private static X509Certificate2 GetCertifiateFromStore(string Hostname)
        {

            X509Certificate2Collection certificateCollection = store.Certificates.Find(X509FindType.FindBySubjectName, Hostname, true);

            foreach (var certificate in certificateCollection)
            {
                if (certificate.SubjectName.Name.StartsWith("CN=" + Hostname) && certificate.IssuerName.Name.StartsWith("CN=Titanium_Proxy_Test_Root"))
                    return certificate;
            }
            return null;
        }

        private static void CreateClientCertificate(string Hostname)
        {

            Process process = new Process();

            // Stop the process from opening a new window
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            // Setup executable and parameters
            process.StartInfo.FileName = @"makecert.exe";
            process.StartInfo.Arguments = @"-pe -ss my -n ""CN=" + Hostname + @""" -sky exchange -in Titanium_Proxy_Test_Root -is my -eku 1.3.6.1.5.5.7.3.1 -cy end -a sha1 -m 132 -b 10/07/2012";

            // Go
            process.Start();
            process.WaitForExit();


        }

    }
}
