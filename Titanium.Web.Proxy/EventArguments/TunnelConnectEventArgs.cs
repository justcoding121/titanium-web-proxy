using System;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.EventArguments
{
    public class TunnelConnectSessionEventArgs : SessionEventArgs
    {
        public bool Excluded { get; set; }

        public bool IsHttpsConnect { get; internal set; }

        internal TunnelConnectSessionEventArgs(int bufferSize, ProxyEndPoint endPoint, ConnectRequest connectRequest, Action<Exception> exceptionFunc) 
            : base(bufferSize, endPoint, exceptionFunc)
        {
            WebSession.Request = connectRequest;
        }
    }
}
