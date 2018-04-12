using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Models
{
    /// <summary>
    /// A proxy end point client is not aware of 
    /// Usefull when requests are redirected to this proxy end point through port forwarding 
    /// </summary>
    public class TransparentProxyEndPoint : ProxyEndPoint
    {
        /// <summary>
        /// Name of the Certificate need to be sent (same as the hostname we want to proxy)
        /// This is valid only when UseServerNameIndication is set to false
        /// </summary>
        public string GenericCertificateName { get; set; }

        /// <summary>
        /// Before Ssl authentication
        /// </summary>
        public event AsyncEventHandler<BeforeSslAuthenticateEventArgs> BeforeSslAuthenticate;
       
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="ipAddress"></param>
        /// <param name="port"></param>
        /// <param name="decryptSsl"></param>
        public TransparentProxyEndPoint(IPAddress ipAddress, int port, bool decryptSsl = true) : base(ipAddress, port, decryptSsl)
        {
            GenericCertificateName = "localhost";
        }

        internal async Task InvokeBeforeSslAuthenticate(ProxyServer proxyServer, BeforeSslAuthenticateEventArgs connectArgs, ExceptionHandler exceptionFunc)
        {
            if (BeforeSslAuthenticate != null)
            {
                await BeforeSslAuthenticate.InvokeAsync(proxyServer, connectArgs, exceptionFunc);
            }
        }
    }
}