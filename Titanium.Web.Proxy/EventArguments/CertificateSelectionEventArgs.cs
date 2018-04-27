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
        ///     The host to which we are authenticating against.
        /// </summary>
        public string TargetHost { get; internal set; }

        /// <summary>
        ///     Local certificates with matching issuers.
        /// </summary>
        public X509CertificateCollection LocalCertificates { get; internal set; }

        /// <summary>
        ///     Remote certificate of the server.
        /// </summary>
        public X509Certificate RemoteCertificate { get; internal set; }

        /// <summary>
        ///     Acceptable issuers mentioned by server.
        /// </summary>
        public string[] AcceptableIssuers { get; internal set; }

        /// <summary>
        ///     Client Certificate we selected.
        /// </summary>
        public X509Certificate ClientCertificate { get; set; }
    }
}
