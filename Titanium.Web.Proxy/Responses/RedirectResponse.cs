using System;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Responses
{
    public class RedirectResponse : Response
    {
        public RedirectResponse()
        {
            ResponseStatusCode = "302";
            ResponseStatusDescription = "Found";
        }
    }
}
