using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy
{
    public partial class ProxyServer
    {
        private async Task<bool> CheckAuthorization(SessionEventArgs session)
        {
            if (AuthenticateUserFunc == null)
            {
                return true;
            }

            var httpHeaders = session.WebSession.Request.Headers;

            try
            {
                var header = httpHeaders.GetFirstHeader(KnownHeaders.ProxyAuthorization);
                if (header == null)
                {
                    session.WebSession.Response = CreateAuthentication407Response("Proxy Authentication Required");
                    return false;
                }

                var headerValueParts = header.Value.Split(ProxyConstants.SpaceSplit);
                if (headerValueParts.Length != 2 || !headerValueParts[0].EqualsIgnoreCase(KnownHeaders.ProxyAuthorizationBasic))
                {
                    //Return not authorized
                    session.WebSession.Response = CreateAuthentication407Response("Proxy Authentication Invalid");
                    return false;
                }

                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(headerValueParts[1]));
                int colonIndex = decoded.IndexOf(':');
                if (colonIndex == -1)
                {
                    //Return not authorized
                    session.WebSession.Response = CreateAuthentication407Response("Proxy Authentication Invalid");
                    return false;
                }

                string username = decoded.Substring(0, colonIndex);
                string password = decoded.Substring(colonIndex + 1);
                bool authenticated = await AuthenticateUserFunc(username, password);
                if (!authenticated)
                {
                    //Return not authorized
                    session.WebSession.Response = CreateAuthentication407Response("Proxy Authentication Invalid");
                }

                return authenticated;
            }
            catch (Exception e)
            {
                ExceptionFunc(new ProxyAuthorizationException("Error whilst authorizing request", session, e, httpHeaders));

                //Return not authorized
                session.WebSession.Response = CreateAuthentication407Response("Proxy Authentication Invalid");
                return false;
            }
        }

        private Response CreateAuthentication407Response(string description)
        {
            var response = new Response
            {
                HttpVersion = HttpHeader.Version11,
                StatusCode = (int)HttpStatusCode.ProxyAuthenticationRequired,
                StatusDescription = description
            };

            response.Headers.AddHeader(KnownHeaders.ProxyAuthenticate, $"Basic realm=\"{ProxyRealm}\"");
            response.Headers.AddHeader(KnownHeaders.ProxyConnection, KnownHeaders.ProxyConnectionClose);

            response.Headers.FixProxyHeaders();
            return response;
        }
    }
}
