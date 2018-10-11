using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    ///     An argument passed on to the user for validating the server certificate
    ///     during SSL authentication.
    /// </summary>
    public class CertificateValidationEventArgs : EventArgs
    {
        /// <summary>
        ///     Server certificate.
        /// </summary>
        public X509Certificate Certificate { get; internal set; }

        /// <summary>
        ///     Certificate chain.
        /// </summary>
        public X509Chain Chain { get; internal set; }

        /// <summary>
        ///     SSL policy errors.
        /// </summary>
        public SslPolicyErrors SslPolicyErrors { get; internal set; }

        /// <summary>
        ///     Is the given server certificate valid?
        /// </summary>
        public bool IsValid { get; set; }
    }
}
