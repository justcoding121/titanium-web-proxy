using System;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    ///     An argument passed on to user for client certificate selection during mutual SSL authentication
    /// </summary>
    public class CertificateSelectionEventArgs : EventArgs
    {
        /// <summary>
        ///     Sender object.
        /// </summary>
        public object Sender { get; internal set; }

        /// <summary>
        ///     Target host.
        /// </summary>
        public string TargetHost { get; internal set; }

        /// <summary>
        ///     Local certificates.
        /// </summary>
        public X509CertificateCollection LocalCertificates { get; internal set; }

        /// <summary>
        ///     Remote certificate.
        /// </summary>
        public X509Certificate RemoteCertificate { get; internal set; }

        /// <summary>
        ///     Acceptable issuers.
        /// </summary>
        public string[] AcceptableIssuers { get; internal set; }

        /// <summary>
        ///     Client Certificate.
        /// </summary>
        public X509Certificate ClientCertificate { get; set; }
    }
}
