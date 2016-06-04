using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.EventArguments
{
    public class CertificateValidationEventArgs : EventArgs, IDisposable
    {
        public X509Certificate Certificate { get; internal set; }
        public X509Chain Chain { get; internal set; }
        public SslPolicyErrors SslPolicyErrors { get; internal set; }

        public bool IsValid { get; set; }

        public void Dispose()
        {
         
        }
    }
}
