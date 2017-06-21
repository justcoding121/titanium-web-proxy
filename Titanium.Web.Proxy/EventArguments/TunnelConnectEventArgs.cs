using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.EventArguments
{
    public class TunnelConnectEventArgs : EventArgs
    {
        public bool IsHttps { get; set; }

        public Request ConnectRequest { get; set; }

        public Request ConnectResponse { get; set; }
    }
}
