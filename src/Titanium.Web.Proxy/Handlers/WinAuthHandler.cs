using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.WinAuth;
using Titanium.Web.Proxy.Network.WinAuth.Security;
using WinAuthHandler = Titanium.Web.Proxy.Network.WinAuth.WinAuthHandler;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     Maximum number of NTLM/Kerberos/Negotiate challenge-response rounds the proxy will perform for a
    ///     single request. The standard NTLM handshake completes in two rounds (initial token + challenge
    ///     response). A cap of three rounds provides one extra margin for servers that re-challenge after
    ///     the connection is marked authenticated (RFC-conformant but still bounded) while preventing an
    ///     infinite retry loop when credentials are persistently rejected.
    /// </summary>
    private const int MaxAuthChallengeRounds = 3;

    /// <summary>Key used to store the per-request auth challenge round counter in <see cref="HttpClient.Data" />.</summary>
    private const string WinAuthRoundCountKey = "WinAuthRoundCount";

    /// <summary>
    ///     possible header names.
    /// </summary>
    private static readonly HashSet<string> authHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WWW-Authenticate",

        // IIS 6.0 messed up names below
        "WWWAuthenticate",
        "NTLMAuthorization",
        "NegotiateAuthorization",
        "KerberosAuthorization"
    };

    private static readonly HashSet<string> proxyAuthHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Proxy-Authenticate"
    };

    /// <summary>
    ///     supported authentication schemes.
    /// </summary>
    private static readonly HashSet<string> authSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "NTLM",
        "Negotiate",
        "Kerberos"
    };

    /// <summary>
    ///     Handle windows NTLM/Kerberos authentication.
    ///     Note: NTLM/Kerberos cannot do a man in middle operation
    ///     we do for HTTPS requests.
    ///     As such we will be sending local credentials of current
    ///     User to server to authenticate requests.
    ///     To disable this set ProxyServer.EnableWinAuth to false.
    /// </summary>
    private async Task Handle401UnAuthorized(SessionEventArgs args) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        string? headerName = null;
        HttpHeader? authHeader = null;

        var response = args.HttpClient.Response;

        // check in non-unique headers first
        var header = response.Headers.NonUniqueHeaders.FirstOrDefault(x => authHeaderNames.Contains(x.Key));

        if (!header.Equals(new KeyValuePair<string, IReadOnlyList<HttpHeader>>())) headerName = header.Key;

        if (headerName != null)
            authHeader = response.Headers.NonUniqueHeaders[headerName]
                .FirstOrDefault(
                    x => authSchemes.Any(y => x.Value.StartsWith(y, StringComparison.OrdinalIgnoreCase)));

        // check in unique headers
        if (authHeader == null)
        {
            headerName = null;

            // check in non-unique headers first
            var uHeader = response.Headers.Headers.FirstOrDefault(x => authHeaderNames.Contains(x.Key));

            if (!uHeader.Equals(new KeyValuePair<string, HttpHeader>())) headerName = uHeader.Key;

            if (headerName != null)
                authHeader = authSchemes.Any(x => response.Headers.Headers[headerName].Value
                    .StartsWith(x, StringComparison.OrdinalIgnoreCase))
                    ? response.Headers.Headers[headerName]
                    : null;
        }

        if (authHeader != null)
        {
            // Bound the total number of auth challenge rounds to prevent infinite retry loops when
            // credentials are persistently rejected or the server continuously re-challenges after the
            // connection is marked authenticated.  ValidateWinAuthState catches most state-machine
            // violations, but allowing Authorized→re-challenge cycles (per its own comment) means a
            // misbehaving server could still loop unboundedly without this additional cap.
            var currentRound = args.HttpClient.Data.TryGetValueAs(WinAuthRoundCountKey, out int storedRound)
                ? storedRound
                : 0;
            if (currentRound >= MaxAuthChallengeRounds)
            {
                await RewriteUnauthorizedResponse(args);
                return;
            }

            args.HttpClient.Data[WinAuthRoundCountKey] = currentRound + 1;

            var scheme = authSchemes.Contains(authHeader.Value) ? authHeader.Value : null;

            var expectedAuthState =
                scheme == null ? State.WinAuthState.InitialToken : State.WinAuthState.Unauthorized;

            if (!WinAuthEndPoint.ValidateWinAuthState(args.HttpClient.Data, expectedAuthState))
            {
                // Invalid state, create proper error message to client
                await RewriteUnauthorizedResponse(args);
                return;
            }

            ProxyMetrics.AuthRoundCompleted(scheme ?? "winauth");

            var request = args.HttpClient.Request;

            // clear any existing headers to avoid confusing bad servers
            request.Headers.RemoveHeader(KnownHeaders.Authorization);

            // initial value will match exactly any of the schemes
            if (scheme != null)
            {
                WinAuthCredentials? credentials = null;
                if (WinAuthCredentialsProvider != null)
                    credentials = await WinAuthCredentialsProvider(args);

                var clientToken = WinAuthHandler.GetInitialAuthToken(request.Host!, scheme, args.HttpClient.Data,
                    credentials);

                var auth = string.Concat(scheme, clientToken);

                // replace existing authorization header if any
                request.Headers.SetOrAddHeaderValue(KnownHeaders.Authorization, auth);

                // don't need to send body for Authorization request
                if (request.HasBody) request.ContentLength = 0;
            }
            else
            {
                // challenge value will start with any of the scheme selected
                scheme = authSchemes.First(x =>
                    authHeader.Value.StartsWith(x, StringComparison.OrdinalIgnoreCase) &&
                    authHeader.Value.Length > x.Length + 1);

                var serverToken = authHeader.Value.Substring(scheme.Length + 1);
                var clientToken = WinAuthHandler.GetFinalAuthToken(request.Host!, serverToken, args.HttpClient.Data);

                var auth = string.Concat(scheme, clientToken);

                // there will be an existing header from initial client request 
                request.Headers.SetOrAddHeaderValue(KnownHeaders.Authorization, auth);

                // send body for final auth request
                if (request.OriginalHasBody) request.ContentLength = request.Body.Length;

                args.HttpClient.Connection.IsWinAuthenticated = true;
            }

            // Need to revisit this.
            // Should we cache all Set-Cookie headers from server during auth process
            // and send it to client after auth?

            // Let ResponseHandler send the updated request
            args.ReRequest = true;
        }
    }

    /// <summary>
    ///     Handles NTLM/Kerberos authentication challenges from an upstream proxy.
    /// </summary>
    private async Task Handle407ProxyAuthorization(SessionEventArgs args) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        if (!args.HttpClient.HasConnection) return;

        var upstreamProxy = args.HttpClient.Connection.UpStreamProxy;
        if (upstreamProxy?.UseDefaultCredentials != true) return;

        var response = args.HttpClient.Response;
        var authHeader = response.Headers.GetHeaders(KnownHeaders.ProxyAuthenticate.String)?
            .FirstOrDefault(x => authSchemes.Any(y =>
                x.Value.Equals(y, StringComparison.OrdinalIgnoreCase) ||
                x.Value.StartsWith(y + " ", StringComparison.OrdinalIgnoreCase)));
        if (authHeader == null) return;

        // Apply the same per-request round cap as Handle401UnAuthorized — see MaxAuthChallengeRounds.
        var currentRound407 = args.HttpClient.Data.TryGetValueAs(WinAuthRoundCountKey, out int stored407)
            ? stored407
            : 0;
        if (currentRound407 >= MaxAuthChallengeRounds)
        {
            await RewriteUnauthorizedResponse(args);
            return;
        }

        args.HttpClient.Data[WinAuthRoundCountKey] = currentRound407 + 1;

        var scheme = authSchemes.Contains(authHeader.Value) ? authHeader.Value : null;
        var expectedAuthState =
            scheme == null ? State.WinAuthState.InitialToken : State.WinAuthState.Unauthorized;

        if (UpstreamProxyWinAuthTokenGenerator == null &&
            !WinAuthEndPoint.ValidateWinAuthState(args.HttpClient.Data, expectedAuthState))
        {
            await RewriteUnauthorizedResponse(args);
            return;
        }

        ProxyMetrics.AuthRoundCompleted(scheme ?? "winauth");

        var request = args.HttpClient.Request;
        request.Headers.RemoveHeader(KnownHeaders.ProxyAuthorization);

        if (scheme != null)
        {
            var clientToken = GenerateUpstreamProxyWinAuthToken(upstreamProxy, scheme, null, args.HttpClient.Data);
            if (string.IsNullOrEmpty(clientToken))
            {
                await RewriteUnauthorizedResponse(args);
                return;
            }

            request.Headers.SetOrAddHeaderValue(KnownHeaders.ProxyAuthorization,
                string.Concat(scheme, clientToken));
            if (request.HasBody) request.ContentLength = 0;
        }
        else
        {
            scheme = authSchemes.First(x =>
                authHeader.Value.StartsWith(x, StringComparison.OrdinalIgnoreCase) &&
                authHeader.Value.Length > x.Length &&
                char.IsWhiteSpace(authHeader.Value[x.Length]));

            var serverToken = authHeader.Value.Substring(scheme.Length).Trim();
            var clientToken =
                GenerateUpstreamProxyWinAuthToken(upstreamProxy, scheme, serverToken, args.HttpClient.Data);
            if (string.IsNullOrEmpty(clientToken))
            {
                await RewriteUnauthorizedResponse(args);
                return;
            }

            request.Headers.SetOrAddHeaderValue(KnownHeaders.ProxyAuthorization,
                string.Concat(scheme, clientToken));
            if (request.OriginalHasBody) request.ContentLength = request.Body.Length;

            args.HttpClient.Connection.IsWinAuthenticated = true;
        }

        args.ReRequest = true;
    }

    /// <summary>
    ///     Rewrites the response body for failed authentication
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    private static async Task RewriteUnauthorizedResponse(SessionEventArgs args)
    {
        var response = args.HttpClient.Response;

        // Strip authentication headers to avoid credentials prompt in client web browser
        foreach (var authHeaderName in authHeaderNames) response.Headers.RemoveHeader(authHeaderName);
        foreach (var proxyAuthHeaderName in proxyAuthHeaderNames) response.Headers.RemoveHeader(proxyAuthHeaderName);

        // Add custom div to body to clarify that the proxy (not the client browser) failed authentication
        var authErrorMessage =
            "<div class=\"inserted-by-proxy\"><h2>NTLM authentication through Titanium.Web.Proxy (" +
            args.ClientLocalEndPoint +
            ") failed. Please check credentials.</h2></div>";
        var originalErrorMessage =
            "<div class=\"inserted-by-proxy\"><h3>Response from remote web server below.</h3></div><br/>";
        var body = await args.GetResponseBodyAsString(args.CancellationTokenSource.Token);
        var idx = body.IndexOfIgnoreCase("<body>");
        if (idx >= 0)
        {
            var bodyPos = idx + "<body>".Length;
            body = body.Insert(bodyPos, authErrorMessage + originalErrorMessage);
        }
        else
        {
            // Cannot parse response body, replace it
            body =
                "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd\">" +
                "<html xmlns=\"http://www.w3.org/1999/xhtml\">" +
                "<body>" +
                authErrorMessage +
                "</body>" +
                "</html>";
        }

        args.SetResponseBodyString(body);
    }
}