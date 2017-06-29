using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy
{
    public partial class ProxyServer
    {
        private async Task<bool> CheckAuthorization(HttpResponseWriter clientStreamWriter, SessionEventArgs session)
        {
            if (AuthenticateUserFunc == null)
            {
                return true;
            }

            var httpHeaders = session.WebSession.Request.RequestHeaders.ToArray();

            try
            {
                var header = httpHeaders.FirstOrDefault(t => t.Name == "Proxy-Authorization");
                if (header == null)
                {
                    session.WebSession.Response = await SendAuthentication407Response(clientStreamWriter, "Proxy Authentication Required");
                    return false;
                }

                string headerValue = header.Value.Trim();
                if (!headerValue.StartsWith("basic", StringComparison.CurrentCultureIgnoreCase))
                {
                    //Return not authorized
                    session.WebSession.Response = await SendAuthentication407Response(clientStreamWriter, "Proxy Authentication Invalid");
                    return false;
                }

                headerValue = headerValue.Substring(5).Trim();

                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(headerValue));
                if (decoded.Contains(":") == false)
                {
                    //Return not authorized
                    session.WebSession.Response = await SendAuthentication407Response(clientStreamWriter, "Proxy Authentication Invalid");
                    return false;
                }

                string username = decoded.Substring(0, decoded.IndexOf(':'));
                string password = decoded.Substring(decoded.IndexOf(':') + 1);
                return await AuthenticateUserFunc(username, password);
            }
            catch (Exception e)
            {
                ExceptionFunc(new ProxyAuthorizationException("Error whilst authorizing request", e, httpHeaders));

                //Return not authorized
                session.WebSession.Response = await SendAuthentication407Response(clientStreamWriter, "Proxy Authentication Invalid");
                return false;
            }
        }

        private async Task<Response> SendAuthentication407Response(HttpResponseWriter clientStreamWriter, string description)
        {
            var response = new Response
            {
                HttpVersion = HttpHeader.Version11,
                ResponseStatusCode = (int)HttpStatusCode.ProxyAuthenticationRequired,
                ResponseStatusDescription = description
            };

            response.ResponseHeaders.AddHeader("Proxy-Authenticate", $"Basic realm=\"{ProxyRealm}\"");
            response.ResponseHeaders.AddHeader("Proxy-Connection", "close");

            await clientStreamWriter.WriteResponseAsync(response);
            return response;
        }
    }
}
