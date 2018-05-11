using System;
using System.Threading;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    ///     A class that wraps the state when a tunnel connect event happen for Explicit endpoints.
    /// </summary>
    public class TunnelConnectSessionEventArgs : SessionEventArgsBase
    {
        private bool? isHttpsConnect;

        internal TunnelConnectSessionEventArgs(ProxyServer server, ProxyEndPoint endPoint, ConnectRequest connectRequest,
            CancellationTokenSource cancellationTokenSource)
            : base(server, endPoint, cancellationTokenSource, connectRequest)
        {
            WebSession.ConnectRequest = connectRequest;
        }

        /// <summary>
        ///     Should we decrypt the Ssl or relay it to server?
        ///     Default is true.
        /// </summary>
        public bool DecryptSsl { get; set; } = true;

        /// <summary>
        ///     When set to true it denies the connect request with a Forbidden status.
        /// </summary>
        public bool DenyConnect { get; set; }

        /// <summary>
        ///     Is this a connect request to secure HTTP server? Or is it to someother protocol.
        /// </summary>
        public bool IsHttpsConnect
        {
            get => isHttpsConnect ??
                   throw new Exception("The value of this property is known in the BeforeTunnectConnectResponse event");

            internal set => isHttpsConnect = value;
        }
    }
}
