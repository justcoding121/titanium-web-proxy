using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy
{
    public partial class ProxyServer
    {

        private async Task<bool> CheckAuthorization(StreamWriter clientStreamWriter, IEnumerable<HttpHeader> Headers)
        {
            if (AuthenticateUserFunc == null)
            {
                return true;
            }
            try
            {
                if (!Headers.Where(t => t.Name == "Proxy-Authorization").Any())
                {

                    await WriteResponseStatus(new Version(1, 1), "407",
                                "Proxy Authentication Required", clientStreamWriter);
                    var response = new Response();
                    response.ResponseHeaders = new Dictionary<string, HttpHeader>();
                    response.ResponseHeaders.Add("Proxy-Authenticate", new HttpHeader("Proxy-Authenticate", "Basic realm=\"TitaniumProxy\""));
                    response.ResponseHeaders.Add("Proxy-Connection", new HttpHeader("Proxy-Connection", "close"));
                    await WriteResponseHeaders(clientStreamWriter, response);

                    await clientStreamWriter.WriteLineAsync();
                    return false;
                }
                else
                {
                    var headerValue = Headers.Where(t => t.Name == "Proxy-Authorization").FirstOrDefault().Value.Trim();
                    if (!headerValue.ToLower().StartsWith("basic"))
                    {
                        //Return not authorized
                        await WriteResponseStatus(new Version(1, 1), "407",
                             "Proxy Authentication Invalid", clientStreamWriter);
                        var response = new Response();
                        response.ResponseHeaders = new Dictionary<string, HttpHeader>();
                        response.ResponseHeaders.Add("Proxy-Authenticate", new HttpHeader("Proxy-Authenticate", "Basic realm=\"TitaniumProxy\""));
                        response.ResponseHeaders.Add("Proxy-Connection", new HttpHeader("Proxy-Connection", "close"));
                        await WriteResponseHeaders(clientStreamWriter, response);

                        await clientStreamWriter.WriteLineAsync();
                        return false;
                    }
                    headerValue = headerValue.Substring(5).Trim();

                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(headerValue));
                    if (decoded.Contains(":") == false)
                    {
                        //Return not authorized
                        await WriteResponseStatus(new Version(1, 1), "407",
                             "Proxy Authentication Invalid", clientStreamWriter);
                        var response = new Response();
                        response.ResponseHeaders = new Dictionary<string, HttpHeader>();
                        response.ResponseHeaders.Add("Proxy-Authenticate", new HttpHeader("Proxy-Authenticate", "Basic realm=\"TitaniumProxy\""));
                        response.ResponseHeaders.Add("Proxy-Connection", new HttpHeader("Proxy-Connection", "close"));
                        await WriteResponseHeaders(clientStreamWriter, response);

                        await clientStreamWriter.WriteLineAsync();
                        return false;
                    }
                    var username = decoded.Substring(0, decoded.IndexOf(':'));
                    var password = decoded.Substring(decoded.IndexOf(':') + 1);
                    return await AuthenticateUserFunc(username, password).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                ExceptionFunc(e);
                //Return not authorized
                await WriteResponseStatus(new Version(1, 1), "407",
                             "Proxy Authentication Invalid", clientStreamWriter);
                var response = new Response();
                response.ResponseHeaders = new Dictionary<string, HttpHeader>();
                response.ResponseHeaders.Add("Proxy-Authenticate", new HttpHeader("Proxy-Authenticate", "Basic realm=\"TitaniumProxy\""));
                response.ResponseHeaders.Add("Proxy-Connection", new HttpHeader("Proxy-Connection", "close"));
                await WriteResponseHeaders(clientStreamWriter, response);

                await clientStreamWriter.WriteLineAsync();
                return false;
            }

        }
    }
}
