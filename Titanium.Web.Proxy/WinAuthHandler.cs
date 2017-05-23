using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.WinAuth;

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

                //initial value will match exactly any of the schemes
                if (scheme != null)
                {
                    var clientToken = WinAuthHandler.GetInitialAuthToken(args.WebSession.Request.Host, scheme, args.Id);
                    args.WebSession.Request.RequestHeaders.Add("Authorization", new HttpHeader("Authorization", string.Concat(scheme, clientToken)));
                }
                //challenge value will start with any of the scheme selected
                else
                {
                    scheme = authSchemes.FirstOrDefault(x => authHeader.Value.StartsWith(x, StringComparison.OrdinalIgnoreCase));

                    var serverToken = authHeader.Value.Substring(scheme.Length + 1);
                    var clientToken = WinAuthHandler.GetFinalAuthToken(args.WebSession.Request.Host, serverToken, args.Id);


                    args.WebSession.Request.RequestHeaders["Authorization"]
                        = new HttpHeader("Authorization", string.Concat(scheme, clientToken));
                }

                //clear current response
                await args.ClearResponse();
                var disposed = await HandleHttpSessionRequestInternal(args.WebSession.ServerConnection, args, false);
                return disposed;
            }

            return false;
        }

    }
}

