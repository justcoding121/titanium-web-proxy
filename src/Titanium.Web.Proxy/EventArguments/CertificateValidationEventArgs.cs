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
        public CertificateValidationEventArgs(SessionEventArgsBase session, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            Session = session;
            Certificate = certificate;
            Chain = chain;
            SslPolicyErrors = sslPolicyErrors;
        }

        /// <value>
        ///     The session.
        /// </value>
        public SessionEventArgsBase Session { get; }

        /// <summary>
        ///     Server certificate.
        /// </summary>
        public X509Certificate Certificate { get; }

        /// <summary>
        ///     Certificate chain.
        /// </summary>
        public X509Chain Chain { get; }

        /// <summary>
        ///     SSL policy errors.
        /// </summary>
        public SslPolicyErrors SslPolicyErrors { get; }

        /// <summary>
        ///     Is the given server certificate valid?
        /// </summary>
        public bool IsValid { get; set; }
    }
}
