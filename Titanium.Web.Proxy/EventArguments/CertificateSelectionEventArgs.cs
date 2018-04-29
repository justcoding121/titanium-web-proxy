using System;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    ///     An argument passed on to user for client certificate selection during mutual SSL authentication.
    /// </summary>
    public class CertificateSelectionEventArgs : EventArgs
    {
        /// <summary>
        ///     The proxy server instance.
        /// </summary>
        public object Sender { get; internal set; }

        /// <summary>
        ///     The remote hostname to which we are authenticating against.
        /// </summary>
        public string TargetHost { get; internal set; }

        /// <summary>
        ///     Local certificates in store with matching issuers requested by TargetHost website.
        /// </summary>
        public X509CertificateCollection LocalCertificates { get; internal set; }

        /// <summary>
        ///     Certificate of the remote server.
        /// </summary>
        public X509Certificate RemoteCertificate { get; internal set; }

        /// <summary>
        ///     Acceptable issuers as listed by remoted server.
        /// </summary>
        public string[] AcceptableIssuers { get; internal set; }

        /// <summary>
        ///     Client Certificate we selected. Set this value to override.
        /// </summary>
        public X509Certificate ClientCertificate { get; set; }
    }
}
