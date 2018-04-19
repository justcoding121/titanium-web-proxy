using System;
using System.Threading;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.EventArguments
{
    public class TunnelConnectSessionEventArgs : SessionEventArgsBase
    {
        private bool? isHttpsConnect;

        internal TunnelConnectSessionEventArgs(int bufferSize, ProxyEndPoint endPoint, ConnectRequest connectRequest,
            CancellationTokenSource cancellationTokenSource, ExceptionHandler exceptionFunc)
            : base(bufferSize, endPoint, cancellationTokenSource, connectRequest, exceptionFunc)
        {
            WebSession.ConnectRequest = connectRequest;
        }

        public bool DecryptSsl { get; set; } = true;

        /// <summary>
        ///     Denies the connect request with a Forbidden status
        /// </summary>
        public bool DenyConnect { get; set; }

        public bool IsHttpsConnect
        {
            get => isHttpsConnect ?? throw new Exception("The value of this property is known in the BeforeTunnectConnectResponse event");
            internal set => isHttpsConnect = value;
        }
    }
}
