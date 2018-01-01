using System;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.EventArguments
{
    public class TunnelConnectSessionEventArgs : SessionEventArgs
    {
        public bool IsHttpsConnect { get; set; }

        internal TunnelConnectSessionEventArgs(int bufferSize, ProxyEndPoint endPoint, ConnectRequest connectRequest, Action<Exception> exceptionFunc) 
            : base(bufferSize, endPoint, null, exceptionFunc)
        {
            WebSession.Request = connectRequest;
        }
    }
}
