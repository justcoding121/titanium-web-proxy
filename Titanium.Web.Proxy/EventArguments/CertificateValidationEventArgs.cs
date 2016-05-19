using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.EventArguments
{
    public class CertificateValidationEventArgs : EventArgs, IDisposable
    {
        public string HostName => Session.WebSession.Request.Host;

        public SessionEventArgs Session { get; internal set; }

        public X509Certificate Certificate { get; internal set; }
        public X509Chain Chain { get; internal set; }
        public SslPolicyErrors SslPolicyErrors { get; internal set; }

        public bool IsValid { get; set; }

        public void Dispose()
        {
         
        }
    }
}
