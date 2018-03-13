using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Models
{
    /// <summary>
    /// A proxy endpoint that the client is aware of 
    /// So client application know that it is communicating with a proxy server
    /// </summary>
    public class ExplicitProxyEndPoint : ProxyEndPoint
    {
        internal bool IsSystemHttpProxy { get; set; }

        internal bool IsSystemHttpsProxy { get; set; }

        /// <summary>
        /// Generic certificate to use for SSL decryption.
        /// </summary>
        public X509Certificate2 GenericCertificate { get; set; }

        /// <summary>
        /// Intercept tunnel connect request
        /// Valid only for explicit endpoints
        /// Set the <see cref="TunnelConnectSessionEventArgs.Excluded"/> property to true if this HTTP connect request should'nt be decrypted and instead be relayed
        /// </summary>
        public event AsyncEventHandler<TunnelConnectSessionEventArgs> TunnelConnectRequest;

        /// <summary>
        /// Intercept tunnel connect response
        /// Valid only for explicit endpoints
        /// </summary>
        public event AsyncEventHandler<TunnelConnectSessionEventArgs> TunnelConnectResponse;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="ipAddress"></param>
        /// <param name="port"></param>
        /// <param name="enableSsl"></param>
        public ExplicitProxyEndPoint(IPAddress ipAddress, int port, bool enableSsl) : base(ipAddress, port, enableSsl)
        {
        }

        internal async Task InvokeTunnectConnectRequest(ProxyServer proxyServer, TunnelConnectSessionEventArgs connectArgs, Action<Exception> exceptionFunc)
        {
            if (TunnelConnectRequest != null)
            {
                await TunnelConnectRequest.InvokeAsync(proxyServer, connectArgs, exceptionFunc);
            }
        }

        internal async Task InvokeTunnectConnectResponse(ProxyServer proxyServer, TunnelConnectSessionEventArgs connectArgs, Action<Exception> exceptionFunc, bool isClientHello = false)
        {
            if (TunnelConnectResponse != null)
            {
                connectArgs.IsHttpsConnect = isClientHello;
                await TunnelConnectResponse.InvokeAsync(proxyServer, connectArgs, exceptionFunc);
            }
        }
    }
}