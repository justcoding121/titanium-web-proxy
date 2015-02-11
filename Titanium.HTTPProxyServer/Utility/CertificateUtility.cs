using System;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using System.Threading;

namespace Titanium.HTTPProxyServer
{
    public partial class ProxyServer
    {
        static X509Store store;
        private static X509Certificate2 getCertificate(string hostname)
        {
           
                if (store == null)
                {
                    store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadOnly);
                }
                var CachedCertificate = getCertifiateFromStore(hostname); 
                
                if(CachedCertificate==null) 
                CreateClientCertificate(hostname);

                return getCertifiateFromStore(hostname);

           
        }
        public static X509Certificate2 getCertifiateFromStore(string hostname)
        {
   
              X509Certificate2Collection certificateCollection = store.Certificates.Find(X509FindType.FindBySubjectName, hostname, true);

                foreach(var certificate in certificateCollection)
                {
                    if (certificate.SubjectName.Name.StartsWith("CN=" + hostname) && certificate.IssuerName.Name.StartsWith("CN=Titanium_Proxy_Test_Root"))
                        return certificate;
                }
                return null;
        }

        public static void CreateClientCertificate(string hostname)
        {
           
            Process process = new Process();

            // Stop the process from opening a new window
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            // Setup executable and parameters
            process.StartInfo.FileName = @"makecert.exe";
            process.StartInfo.Arguments = @"-pe -ss my -n ""CN=" + hostname + @""" -sky exchange -in Titanium_Proxy_Test_Root -is my -eku 1.3.6.1.5.5.7.3.1 -cy end -a sha1 -m 132 -b 10/07/2012";

            // Go
            process.Start();
            process.WaitForExit();
           

        }

    }
}
