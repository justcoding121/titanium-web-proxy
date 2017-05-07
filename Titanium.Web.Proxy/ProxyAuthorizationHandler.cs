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

        private async Task<bool> CheckAuthorization(StreamWriter clientStreamWriter, IEnumerable<HttpHeader> headers)
        {
            if (AuthenticateUserFunc == null)
            {
                return true;
            }

            var httpHeaders = headers as HttpHeader[] ?? headers.ToArray();

            try
            {
                if (httpHeaders.All(t => t.Name != "Proxy-Authorization"))
                {

                    await WriteResponseStatus(new Version(1, 1), "407",
                                "Proxy Authentication Required", clientStreamWriter);
                    var response = new Response
                    {
                        ResponseHeaders = new Dictionary<string, HttpHeader>
                        {
                            {
                                "Proxy-Authenticate",
                                new HttpHeader("Proxy-Authenticate", "Basic realm=\"TitaniumProxy\"")
                            },
                            {"Proxy-Connection", new HttpHeader("Proxy-Connection", "close")}
                        }
                    };
                    await WriteResponseHeaders(clientStreamWriter, response);

                    await clientStreamWriter.WriteLineAsync();
                    return false;
                }
                var header = httpHeaders.FirstOrDefault(t => t.Name == "Proxy-Authorization");
                if (null == header) throw new NullReferenceException();
                var headerValue = header.Value.Trim();
                if (!headerValue.ToLower().StartsWith("basic"))
                {
                    //Return not authorized
                    await WriteResponseStatus(new Version(1, 1), "407",
                        "Proxy Authentication Invalid", clientStreamWriter);
                    var response = new Response
                    {
                        ResponseHeaders = new Dictionary<string, HttpHeader>
                        {
                            {
                                "Proxy-Authenticate",
                                new HttpHeader("Proxy-Authenticate", "Basic realm=\"TitaniumProxy\"")
                            },
                            {"Proxy-Connection", new HttpHeader("Proxy-Connection", "close")}
                        }
                    };
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
                    var response = new Response
                    {
                        ResponseHeaders = new Dictionary<string, HttpHeader>
                        {
                            {
                                "Proxy-Authenticate",
                                new HttpHeader("Proxy-Authenticate", "Basic realm=\"TitaniumProxy\"")
                            },
                            {"Proxy-Connection", new HttpHeader("Proxy-Connection", "close")}
                        }
                    };
                    await WriteResponseHeaders(clientStreamWriter, response);

                    await clientStreamWriter.WriteLineAsync();
                    return false;
                }
                var username = decoded.Substring(0, decoded.IndexOf(':'));
                var password = decoded.Substring(decoded.IndexOf(':') + 1);
                return await AuthenticateUserFunc(username, password).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                ExceptionFunc(new ProxyAuthorizationException("Error whilst authorizing request", e, httpHeaders));
                //Return not authorized
                await WriteResponseStatus(new Version(1, 1), "407",
                             "Proxy Authentication Invalid", clientStreamWriter);
                var response = new Response
                {
                    ResponseHeaders = new Dictionary<string, HttpHeader>
                    {
                        {"Proxy-Authenticate", new HttpHeader("Proxy-Authenticate", "Basic realm=\"TitaniumProxy\"")},
                        {"Proxy-Connection", new HttpHeader("Proxy-Connection", "close")}
                    }
                };
                await WriteResponseHeaders(clientStreamWriter, response);

                await clientStreamWriter.WriteLineAsync();
                return false;
            }

        }
    }
}
