using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.WinAuth;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy
{
    public partial class ProxyServer
    {
        //possible header names
        private static List<string> authHeaderNames
          = new List<string>() {
                "WWW-Authenticate",
                //IIS 6.0 messed up names below
                "WWWAuthenticate",
                "NTLMAuthorization",
                "NegotiateAuthorization",
                "KerberosAuthorization"
          };

        private static List<string> authSchemes
            = new List<string>() {
                "NTLM",
                "Negotiate",
                "Kerberos"
            };

        /// <summary>
        /// Handle windows NTLM authentication 
        /// Can expand this for Kerberos in future
        /// Note: NTLM/Kerberos cannot do a man in middle operation
        /// we do for HTTPS requests. 
        /// As such we will be sending local credentials of current
        /// User to server to authenticate requests. 
        /// To disable this set ProxyServer.EnableWinAuth to false
        /// </summary>
        internal async Task<bool> Handle401UnAuthorized(SessionEventArgs args)
        {
            string headerName = null;
            HttpHeader authHeader = null;

            //check in non-unique headers first
            var header = args.WebSession.Response
                .NonUniqueResponseHeaders
                .FirstOrDefault(x =>
                authHeaderNames.Any(y => x.Key.Equals(y, StringComparison.OrdinalIgnoreCase)));

            if (!header.Equals(new KeyValuePair<string, List<HttpHeader>>()))
            {
                headerName = header.Key;
            }
  
            if (headerName != null)
            {
                authHeader = args.WebSession.Response
                    .NonUniqueResponseHeaders[headerName]
                    .Where(x => authSchemes.Any(y => x.Value.StartsWith(y, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault();
            }

            //check in unique headers
            if (authHeader == null)
            {
                //check in non-unique headers first
                var uHeader = args.WebSession.Response
                    .ResponseHeaders
                    .FirstOrDefault(x =>
                     authHeaderNames.Any(y => x.Key.Equals(y, StringComparison.OrdinalIgnoreCase)));

                if (!uHeader.Equals(new KeyValuePair<string, HttpHeader>()))
                {
                    headerName = uHeader.Key;
                }

                if (headerName != null)
                {
                    authHeader = authSchemes.Any(x => args.WebSession.Response
                    .ResponseHeaders[headerName].Value.StartsWith(x, StringComparison.OrdinalIgnoreCase)) ?
                     args.WebSession.Response.ResponseHeaders[headerName] : null;
                }
            }

            if (authHeader != null)
            {
                var scheme = authSchemes.FirstOrDefault(x => authHeader.Value.Equals(x, StringComparison.OrdinalIgnoreCase));

                //clear any existing headers to avoid confusing bad servers
                if (args.WebSession.Request.NonUniqueRequestHeaders.ContainsKey("Authorization"))
                {
                    args.WebSession.Request.NonUniqueRequestHeaders.Remove("Authorization");
                }

                //initial value will match exactly any of the schemes
                if (scheme != null)
                {
                    var clientToken = WinAuthHandler.GetInitialAuthToken(args.WebSession.Request.Host, scheme, args.Id);

                    var auth = new HttpHeader("Authorization", string.Concat(scheme, clientToken));
                   
                    //replace existing authorization header if any
                    if (args.WebSession.Request.RequestHeaders.ContainsKey("Authorization"))
                    {
                        args.WebSession.Request.RequestHeaders["Authorization"] = auth;
                    }
                    else
                    {
                        args.WebSession.Request.RequestHeaders.Add("Authorization", auth);
                    }

                    //don't need to send body for Authorization request
                    if(args.WebSession.Request.HasBody)
                    {
                        args.WebSession.Request.ContentLength = 0;
                    }
                    
                }
                //challenge value will start with any of the scheme selected
                else
                {
                    scheme = authSchemes.FirstOrDefault(x => authHeader.Value.StartsWith(x, StringComparison.OrdinalIgnoreCase)
                    && authHeader.Value.Length > x.Length + 1);

                    var serverToken = authHeader.Value.Substring(scheme.Length + 1);
                    var clientToken = WinAuthHandler.GetFinalAuthToken(args.WebSession.Request.Host, serverToken, args.Id);

                    //there will be an existing header from initial client request 
                    args.WebSession.Request.RequestHeaders["Authorization"]
                        = new HttpHeader("Authorization", string.Concat(scheme, clientToken));

                    //send body for final auth request
                    if (args.WebSession.Request.HasBody)
                    {
                        args.WebSession.Request.ContentLength 
                            = args.WebSession.Request.RequestBody.Length;
                    }
                        
                }

                //Need to revisit this.
                //Should we cache all Set-Cokiee headers from server during auth process
                //and send it to client after auth?

                //clear current server response
                await args.ClearResponse();

                //request again with updated authorization header
                //and server cookies
                var disposed = await HandleHttpSessionRequestInternal(args.WebSession.ServerConnection, args, false);
                return disposed;
            }

            return false;
        }

    }
}

