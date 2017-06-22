using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.EventArguments
{
    public class TunnelConnectSessionEventArgs : SessionEventArgs
    {
        public bool IsHttps { get; set; }

        public TunnelConnectSessionEventArgs() : base(0, null)
        {
            
        }
    }
}
