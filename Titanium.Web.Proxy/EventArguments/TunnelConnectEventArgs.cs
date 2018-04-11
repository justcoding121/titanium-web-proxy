using System;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.EventArguments
{
    public class TunnelConnectSessionEventArgs : SessionEventArgs
    {
        public bool DecryptSsl { get; set; } = true;

        public bool IsHttpsConnect { get; internal set; }

        internal TunnelConnectSessionEventArgs(int bufferSize, ProxyEndPoint endPoint, ConnectRequest connectRequest, ExceptionHandler exceptionFunc) 
            : base(bufferSize, endPoint, exceptionFunc, connectRequest)
        {
        }
    }
}
