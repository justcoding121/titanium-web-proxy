using System;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Responses
{
    public class OkResponse : Response
    {
        public OkResponse()
        {
            ResponseStatusCode = "200";
            ResponseStatusDescription = "Ok";

            ResponseHeaders.Add(new HttpHeader("Timestamp", DateTime.Now.ToString()));
            ResponseHeaders.Add(new HttpHeader("content-length", DateTime.Now.ToString()));
            ResponseHeaders.Add(new HttpHeader("Cache-Control", "no-cache, no-store, must-revalidate"));
            ResponseHeaders.Add(new HttpHeader("Pragma", "no-cache"));
            ResponseHeaders.Add(new HttpHeader("Expires", "0"));
        }
    }
}
