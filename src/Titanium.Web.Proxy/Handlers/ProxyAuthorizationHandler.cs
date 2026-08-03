using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    private const string ProxyAuthenticationInvalid = "Proxy Authentication Invalid";

    /// <summary>
    ///     Callback to authorize clients of this proxy instance.
    /// </summary>
    /// <param name="session">The session event arguments.</param>
    /// <returns>True if authorized.</returns>
    private async Task<bool> CheckAuthorization(SessionEventArgsBase session)
    {
        var basicAuthenticate = ProxyBasicAuthenticateFunc;
        var schemeAuthenticate = ProxySchemeAuthenticateFunc;

        // If we are not authorizing clients return true
        if (basicAuthenticate is null && schemeAuthenticate is null) return true;

        var httpHeaders = session.HttpClient.Request.Headers;

        try
        {
            var headerObj = httpHeaders.GetFirstHeader(KnownHeaders.ProxyAuthorization);
            if (headerObj == null)
            {
                session.HttpClient.Response = CreateAuthentication407Response("Proxy Authentication Required");
                return false;
            }

            var header = headerObj.Value;
            var firstSpace = header.IndexOf(' ');

            // header value should contain exactly 1 space
            if (firstSpace == -1 || header.IndexOf(' ', firstSpace + 1) != -1)
            {
                // Return not authorized
                session.HttpClient.Response = CreateAuthentication407Response(ProxyAuthenticationInvalid);
                return false;
            }

            var authenticationType = header.AsMemory(0, firstSpace);
            var credentials = header.AsMemory(firstSpace + 1);

            // Prefer basic when configured; otherwise use scheme auth (guaranteed non-null here).
            if (basicAuthenticate is not null)
                return await AuthenticateUserBasic(session, authenticationType, credentials,
                    basicAuthenticate);

            var result = await schemeAuthenticate!(session, authenticationType.ToString(),
                credentials.ToString()); // NOSONAR S8969 -- flow proves non-null after dual-null early return

            if (result.Result == ProxyAuthenticationResult.ContinuationNeeded)
            {
                session.HttpClient.Response =
                    CreateAuthentication407Response(ProxyAuthenticationInvalid, result.Continuation);

                return false;
            }

            return result.Result == ProxyAuthenticationResult.Success;
        }
        catch (Exception e)
        {
            OnException(null, new ProxyAuthorizationException("Error whilst authorizing request", session, e,
                httpHeaders));

            // Return not authorized
            session.HttpClient.Response = CreateAuthentication407Response(ProxyAuthenticationInvalid);
            return false;
        }
    }

    private async Task<bool> AuthenticateUserBasic(SessionEventArgsBase session,
        ReadOnlyMemory<char> authenticationType, ReadOnlyMemory<char> credentials,
        Func<SessionEventArgsBase, string, string, Task<bool>> proxyBasicAuthenticateFunc)
    {
        if (!KnownHeaders.ProxyAuthorizationBasic.Equals(authenticationType.Span))
        {
            // Return not authorized
            session.HttpClient.Response = CreateAuthentication407Response(ProxyAuthenticationInvalid);
            return false;
        }

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(credentials.ToString()));
        var colonIndex = decoded.IndexOf(':');
        if (colonIndex == -1)
        {
            // Return not authorized
            session.HttpClient.Response = CreateAuthentication407Response(ProxyAuthenticationInvalid);
            return false;
        }

        var username = decoded.Substring(0, colonIndex);
        var password = decoded.Substring(colonIndex + 1);
        var authenticated = await proxyBasicAuthenticateFunc(session, username, password);
        if (!authenticated)
            session.HttpClient.Response = CreateAuthentication407Response(ProxyAuthenticationInvalid);

        return authenticated;
    }

    /// <summary>
    ///     Create an authentication required response.
    /// </summary>
    /// <param name="description">Response description.</param>
    /// <param name="continuation">The continuation.</param>
    /// <returns></returns>
    private Response CreateAuthentication407Response(string description, string? continuation = null)
    {
        var response = new Response
        {
            HttpVersion = HttpHeader.Version11,
            StatusCode = (int)HttpStatusCode.ProxyAuthenticationRequired,
            StatusDescription = description
        };

        if (!string.IsNullOrWhiteSpace(continuation)) return CreateContinuationResponse(response, continuation);

        if (ProxyBasicAuthenticateFunc != null)
            response.Headers.AddHeader(KnownHeaders.ProxyAuthenticate, $"Basic realm=\"{ProxyAuthenticationRealm}\"");

        if (ProxySchemeAuthenticateFunc != null)
            foreach (var scheme in ProxyAuthenticationSchemes)
                response.Headers.AddHeader(KnownHeaders.ProxyAuthenticate, scheme);

        response.Headers.AddHeader(KnownHeaders.ProxyConnection, KnownHeaders.ProxyConnectionClose);

        response.Headers.FixProxyHeaders();
        return response;
    }

    private static Response CreateContinuationResponse(Response response, string continuation)
    {
        response.Headers.AddHeader(KnownHeaders.ProxyAuthenticate, continuation);

        response.Headers.AddHeader(KnownHeaders.ProxyConnection, KnownHeaders.ConnectionKeepAlive);

        response.Headers.FixProxyHeaders();

        return response;
    }
}