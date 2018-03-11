using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Models
{
    /// <summary>
    /// An abstract endpoint where the proxy listens
    /// </summary>
    public abstract class ProxyEndPoint
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="ipAddress"></param>
        /// <param name="port"></param>
        /// <param name="enableSsl"></param>
        protected ProxyEndPoint(IPAddress ipAddress, int port, bool enableSsl)
        {
            IpAddress = ipAddress;
            Port = port;
            EnableSsl = enableSsl;
        }
        
        /// <summary>
        /// underlying TCP Listener object
        /// </summary>
        internal TcpListener Listener { get; set; }

        /// <summary>
        /// Ip Address we are listening.
        /// </summary>
        public IPAddress IpAddress { get; internal set; }

        /// <summary>
        /// Port we are listening.
        /// </summary>
        public int Port { get; internal set; }

        /// <summary>
        /// Enable SSL?
        /// </summary>
        public bool EnableSsl { get; internal set; }

        /// <summary>
        /// Is IPv6 enabled?
        /// </summary>
        public bool IpV6Enabled => Equals(IpAddress, IPAddress.IPv6Any)
                                   || Equals(IpAddress, IPAddress.IPv6Loopback)
                                   || Equals(IpAddress, IPAddress.IPv6None);
    }

    /// <summary>
    /// A proxy endpoint that the client is aware of 
    /// So client application know that it is communicating with a proxy server
    /// </summary>
    public class ExplicitProxyEndPoint : ProxyEndPoint
    {
        internal List<Regex> ExcludedHttpsHostNameRegexList;
        internal List<Regex> IncludedHttpsHostNameRegexList;

        internal bool IsSystemHttpProxy { get; set; }

        internal bool IsSystemHttpsProxy { get; set; }

        /// <summary>
        /// Generic certificate to use for SSL decryption.
        /// </summary>
        public X509Certificate2 GenericCertificate { get; set; }

        /// <summary>
        /// Return true if this HTTP connect request should'nt be decrypted and instead be relayed
        /// Valid only for explicit endpoints
        /// </summary>
        public Func<string, Task<bool>> BeforeTunnelConnect;

        /// <summary>
        /// Intercept tunnel connect request
        /// Valid only for explicit endpoints
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

        internal async Task InvokeTunnectConnectResponse(ProxyServer proxyServer, TunnelConnectSessionEventArgs connectArgs, Action<Exception> exceptionFunc)
        {
            if (TunnelConnectResponse != null)
            {
                await TunnelConnectResponse.InvokeAsync(proxyServer, connectArgs, exceptionFunc);
            }
        }

        internal async Task InvokeTunnectConnectResponse(ProxyServer proxyServer, TunnelConnectSessionEventArgs connectArgs, Action<Exception> exceptionFunc, bool isClientHello)
        {
            if (TunnelConnectResponse != null)
            {
                connectArgs.IsHttpsConnect = isClientHello;
                await TunnelConnectResponse.InvokeAsync(proxyServer, connectArgs, exceptionFunc);
            }
        }
    }

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
        /// Constructor.
        /// </summary>
        /// <param name="ipAddress"></param>
        /// <param name="port"></param>
        /// <param name="enableSsl"></param>
        public TransparentProxyEndPoint(IPAddress ipAddress, int port, bool enableSsl) : base(ipAddress, port, enableSsl)
        {
            GenericCertificateName = "localhost";
        }
    }
}
