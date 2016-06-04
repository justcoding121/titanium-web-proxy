using System;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.EventArguments
{
    public class CertificateSelectionEventArgs : EventArgs, IDisposable
    {
        public object sender { get; internal set; }
        public string targetHost { get; internal set; }
        public X509CertificateCollection localCertificates { get; internal set; }
        public X509Certificate remoteCertificate { get; internal set; }
        public string[] acceptableIssuers { get; internal set; }

        public X509Certificate clientCertificate { get; set; }

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}
