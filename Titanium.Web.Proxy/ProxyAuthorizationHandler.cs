using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy
{
    public partial class ProxyServer
    {
        private async Task<bool> CheckAuthorization(StreamWriter clientStreamWriter, Request request)
        {
            if (AuthenticateUserFunc == null)
            {
                return true;
            }

            var httpHeaders = request.RequestHeaders.ToArray();

            try
            {
                if (httpHeaders.All(t => t.Name != "Proxy-Authorization"))
                {
                    await SendAuthentication407Response(clientStreamWriter, "Proxy Authentication Required");
                    return false;
                }

                var header = httpHeaders.FirstOrDefault(t => t.Name == "Proxy-Authorization");
                if (header == null)
                    throw new NullReferenceException();
                string headerValue = header.Value.Trim();
                if (!headerValue.StartsWith("basic", StringComparison.CurrentCultureIgnoreCase))
                {
                    //Return not authorized
                    await SendAuthentication407Response(clientStreamWriter, "Proxy Authentication Invalid");
                    return false;
                }

                headerValue = headerValue.Substring(5).Trim();

                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(headerValue));
                if (decoded.Contains(":") == false)
                {
                    //Return not authorized
                    await SendAuthentication407Response(clientStreamWriter, "Proxy Authentication Invalid");
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
                await SendAuthentication407Response(clientStreamWriter, "Proxy Authentication Invalid");
                return false;
            }
        }

        private async Task SendAuthentication407Response(StreamWriter clientStreamWriter, string description)
        {
            await WriteResponseStatus(HttpHeader.Version11, "407", description, clientStreamWriter);
            var response = new Response();
            response.ResponseHeaders.AddHeader("Proxy-Authenticate", "Basic realm=\"TitaniumProxy\"");
            response.ResponseHeaders.AddHeader("Proxy-Connection", "close");

            await WriteResponseHeaders(clientStreamWriter, response);

            await clientStreamWriter.WriteLineAsync();
        }
    }
}
