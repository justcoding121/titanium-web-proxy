using System;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    /// An argument passed on to user for client certificate selection during mutual SSL authentication
    /// </summary>
    public class CertificateSelectionEventArgs : EventArgs
    {
        public object Sender { get; internal set; }
        public string TargetHost { get; internal set; }
        public X509CertificateCollection LocalCertificates { get; internal set; }
        public X509Certificate RemoteCertificate { get; internal set; }
        public string[] AcceptableIssuers { get; internal set; }

        public X509Certificate ClientCertificate { get; set; }

    }
}
