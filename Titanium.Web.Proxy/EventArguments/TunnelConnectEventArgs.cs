using System;
using System.Threading;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.EventArguments
{
    public class TunnelConnectSessionEventArgs : SessionEventArgsBase
    {
        public bool DecryptSsl { get; set; } = true;

        /// <summary>
        /// Denies the connect request with a Forbidden status 
        /// </summary>
        public bool DenyConnect { get; set; }

        public bool IsHttpsConnect { get; internal set; }

        internal TunnelConnectSessionEventArgs(int bufferSize, ProxyEndPoint endPoint, ConnectRequest connectRequest, ExceptionHandler exceptionFunc, CancellationTokenSource cancellationTokenSource) 
            : base(bufferSize, endPoint, exceptionFunc, connectRequest, cancellationTokenSource)
        {
        }
    }
}
