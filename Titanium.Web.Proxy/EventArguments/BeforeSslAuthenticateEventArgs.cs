using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.EventArguments
{
    public class BeforeSslAuthenticateEventArgs : EventArgs
    {

        public string SniHostName { get; internal set; }

        public bool DecryptSsl { get; set; } = true;

        public bool TerminateSession { get; set; }
    }
}
