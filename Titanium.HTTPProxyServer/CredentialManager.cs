using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Security.Principal;

namespace Titanium.HTTPProxyServer
{
    public class CredentialManager
    {
        public static  Dictionary<string, WindowsPrincipal> Cache { get; set; }
    }
}
