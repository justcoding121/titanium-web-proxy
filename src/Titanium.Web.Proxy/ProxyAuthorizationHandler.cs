using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy
{
    public partial class ProxyServer
    {
        /// <summary>
        ///     Callback to authorize clients of this proxy instance.
        /// </summary>
        /// <param name="session">The session event arguments.</param>
        /// <returns>True if authorized.</returns>
        private async Task<bool> checkAuthorization(SessionEventArgsBase session)
        {
            // If we are not authorizing clients return true
            if (ProxyBasicAuthenticateFunc == null && ProxySchemeAuthenticateFunc == null)
            {
                return true;
            }

            var httpHeaders = session.WebSession.Request.Headers;

            try
            {
                var header = httpHeaders.GetFirstHeader(KnownHeaders.ProxyAuthorization);
                if (header == null)
                {
                    session.WebSession.Response = createAuthentication407Response("Proxy Authentication Required");
                    return false;
                }

                var headerValueParts = header.Value.Split(ProxyConstants.SpaceSplit);

                if (headerValueParts.Length != 2)
                {
                    // Return not authorized
                    session.WebSession.Response = createAuthentication407Response("Proxy Authentication Invalid");
                    return false;
                }

                if (ProxyBasicAuthenticateFunc != null)
                {
                    return await authenticateUserBasic(session, headerValueParts);
                }

                if (ProxySchemeAuthenticateFunc != null)
                {
                    var result = await ProxySchemeAuthenticateFunc(session, headerValueParts[0], headerValueParts[1]);

                    if (result.Result == ProxyAuthenticationResult.ContinuationNeeded)
                    {
                        session.WebSession.Response = createAuthentication407Response("Proxy Authentication Invalid", result.Continuation);

                        return false;
                    }

                    return result.Result == ProxyAuthenticationResult.Success;
                }

                return false;
            }
            catch (Exception e)
            {
                ExceptionFunc(new ProxyAuthorizationException("Error whilst authorizing request", session, e,
                    httpHeaders));

                // Return not authorized
                session.WebSession.Response = createAuthentication407Response("Proxy Authentication Invalid");
                return false;
            }
        }

        private async Task<bool> authenticateUserBasic(SessionEventArgsBase session, string[] headerValueParts)
        {
            if (!headerValueParts[0].EqualsIgnoreCase(KnownHeaders.ProxyAuthorizationBasic))
            {
                // Return not authorized
                session.WebSession.Response = createAuthentication407Response("Proxy Authentication Invalid");
                return false;
            }

            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(headerValueParts[1]));
            int colonIndex = decoded.IndexOf(':');
            if (colonIndex == -1)
            {
                // Return not authorized
                session.WebSession.Response = createAuthentication407Response("Proxy Authentication Invalid");
                return false;
            }

            string username = decoded.Substring(0, colonIndex);
            string password = decoded.Substring(colonIndex + 1);
            bool authenticated = await ProxyBasicAuthenticateFunc(session, username, password);
            if (!authenticated)
            {
                session.WebSession.Response = createAuthentication407Response("Proxy Authentication Invalid");
            }

            return authenticated;
        }

        /// <summary>
        ///     Create an authentication required response.
        /// </summary>
        /// <param name="description">Response description.</param>
        /// <returns></returns>
        private Response createAuthentication407Response(string description, string continuation = null)
        {
            var response = new Response
            {
                HttpVersion = HttpHeader.Version11,
                StatusCode = (int)HttpStatusCode.ProxyAuthenticationRequired,
                StatusDescription = description
            };

            if (!string.IsNullOrWhiteSpace(continuation))
            {
                return createContinuationResponse(response, continuation);
            }

            if (ProxyBasicAuthenticateFunc != null)
            {
                response.Headers.AddHeader(KnownHeaders.ProxyAuthenticate, $"Basic realm=\"{ProxyAuthenticationRealm}\"");
            }

            if (ProxySchemeAuthenticateFunc != null)
            {
                foreach (var scheme in ProxyAuthenticationSchemes)
                {
                    response.Headers.AddHeader(KnownHeaders.ProxyAuthenticate, scheme);
                }
            }

            response.Headers.AddHeader(KnownHeaders.ProxyConnection, KnownHeaders.ProxyConnectionClose);

            response.Headers.FixProxyHeaders();
            return response;
        }

        private Response createContinuationResponse(Response response, string continuation)
        {
            response.Headers.AddHeader(KnownHeaders.ProxyAuthenticate, continuation);

            response.Headers.AddHeader(KnownHeaders.ProxyConnection, KnownHeaders.ConnectionKeepAlive);
            
            response.Headers.FixProxyHeaders();

            return response;
        }
    }
}
