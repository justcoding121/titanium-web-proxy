using System;
using System.Linq;
using System.Net.Sockets;
using System.Security.Authentication;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     Classifies failures that are safe to learn as "tunnel without decrypt"
///     (bot / fingerprint style). ALPN mismatches and plain TCP/DNS failures are excluded.
/// </summary>
internal static class DecryptFailureLearning
{
    internal static bool IsLearnableOriginTlsFailure(Exception? error)
    {
        if (error == null || AlpnNegotiation.IsAlpnNegotiationFailure(error))
            return false;

        // Prefer not to learn pure connectivity failures.
        for (Exception? e = error; e != null; e = e.InnerException)
        {
            if (e is SocketException)
                return false;
        }

        for (Exception? e = error; e != null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     The failure is specifically the client refusing the proxy certificate (untrusted root / pinning),
    ///     not a plain TCP reset. TLS 1.3 often surfaces this on the first decrypted read, after
    ///     <c>AuthenticateAsServerAsync</c> has already returned.
    /// </summary>
    internal static bool IsClientCertificateRejection(Exception? error)
    {
        for (var e = error; e != null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return true;

            // Schannel: SEC_E_UNTRUSTED_ROOT, SEC_E_CERT_UNKNOWN, SEC_E_CERT_EXPIRED.
            if (e is System.ComponentModel.Win32Exception win
                && win.NativeErrorCode is -2146893019 or -2146893017 or -2146893016)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     The client side of the MITM TLS handshake ended with an abort/alert/EOF (the client did not accept
    ///     the proxy certificate or hung up). Excludes cancellation and timeouts, which say more about
    ///     proxy/machine load than about client trust.
    /// </summary>
    internal static bool IsClientHandshakeRejection(Exception? error)
    {
        for (var e = error; e != null; e = e.InnerException)
        {
            if (e is OperationCanceledException or TimeoutException or Exceptions.ProxyTimeoutException)
                return false;
        }

        for (var e = error; e != null; e = e.InnerException)
        {
            if (e is AuthenticationException or System.IO.IOException)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     MITM HTTPS 403/429 from the origin (not synthetic Ok/Respond/GenericResponse) that carries
    ///     a bot-management / challenge marker (<see cref="LooksLikeBotChallenge" />). A bare 403 or 429
    ///     is an ordinary answer (permission denied, expired login, API rate limit) and must not switch
    ///     a whole host to an opaque tunnel.
    /// </summary>
    internal static bool IsLearnableHttpBlock(SessionEventArgs args)
    {
        if (!args.IsHttps || args.Exception is not null)
            return false;

        if (!args.HttpClient.HasResponse)
            return false;

        var response = args.HttpClient.Response;
        if (response.IsSynthetic)
            return false;

        return (response.StatusCode is 403 or 429) && LooksLikeBotChallenge(response);
    }

    private static readonly string[] BotChallengeHeaderNames =
    [
        "cf-mitigated", // Cloudflare: "challenge" when a managed challenge was served
        "x-datadome", "x-datadome-response", "x-dd-b", // DataDome
        "x-amzn-waf-action" // AWS WAF: captcha / challenge
    ];

    private static readonly string[] BotChallengeCookieNames =
    [
        "_abck", "bm_s", "bm_sz", "bm_sv", "ak_bmsc", // Akamai Bot Manager
        "datadome", // DataDome
        "reese84" // Imperva
    ];

    private static readonly string[] BotChallengeCookiePrefixes =
    [
        "incap_ses_", "visid_incap_", // Imperva / Incapsula
        "_px" // HUMAN / PerimeterX
    ];

    /// <summary>
    ///     Headers whose values legitimately mention "challenge" or "captcha" without the response being a
    ///     bot challenge (auth schemes, CSP allow-lists for a captcha widget, CORS lists, ...).
    /// </summary>
    private static readonly string[] NonChallengeHeaderNames =
    [
        "content-security-policy", "content-security-policy-report-only", "permissions-policy", "link",
        "report-to", "nel", "alt-svc", "www-authenticate", "proxy-authenticate", "location", "vary"
    ];

    /// <summary>
    ///     Header-only check (the body is not read on this path) for a response that a bot-management product
    ///     produced, which is what a TLS-fingerprint (JA3 / JA4 / Akamai) denial looks like when the proxy's
    ///     own TLS stack is the reason for the block. Signals, any one of which is enough:
    ///     <list type="bullet">
    ///         <item><description>A vendor header: <c>cf-mitigated</c>, <c>x-datadome*</c>, <c>x-dd-b</c>,
    ///         <c>x-amzn-waf-action</c>, <c>x-kpsdk-*</c> (Kasada).</description></item>
    ///         <item><description><c>Server: AkamaiGHost</c>: the Akamai edge answered itself rather than
    ///         forwarding an origin response.</description></item>
    ///         <item><description>A bot-management cookie being set (<c>_abck</c>, <c>bm_sz</c>, <c>datadome</c>,
    ///         <c>incap_ses_*</c>, <c>_px*</c>, ...).</description></item>
    ///         <item><description>The word "captcha" or "challenge" in a header name or value, other than the
    ///         headers in the skip list (an Expedia block carries
    ///         <c>x-hcom-origin-id: wildcard-challenge-handler</c> and <c>x-app-info: captcha-pwa</c>).</description></item>
    ///     </list>
    ///     A plain 403 or a rate-limit 429 (<c>Retry-After</c>) matches none of them.
    /// </summary>
    internal static bool LooksLikeBotChallenge(Response response)
    {
        foreach (var header in response.Headers)
        {
            if (IsBotChallengeHeader(header.Name, header.Value))
                return true;
        }

        return false;
    }

    private static bool IsBotChallengeHeader(string name, string value)
    {
        if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            return IsBotManagementCookie(value);

        if (name.StartsWith("x-kpsdk-", StringComparison.OrdinalIgnoreCase))
            return true;

        if (BotChallengeHeaderNames.Any(vendor => name.Equals(vendor, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (name.Equals("Server", StringComparison.OrdinalIgnoreCase)
            && value.Contains("AkamaiGHost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsNonChallengeHeader(name))
            return false;

        return MentionsChallenge(name) || MentionsChallenge(value);
    }

    private static bool IsNonChallengeHeader(string name)
    {
        if (name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
            return true;

        return NonChallengeHeaderNames.Any(skip => name.Equals(skip, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MentionsChallenge(string text) =>
        text.Contains("captcha", StringComparison.OrdinalIgnoreCase)
        || text.Contains("challenge", StringComparison.OrdinalIgnoreCase);

    private static bool IsBotManagementCookie(string setCookieValue)
    {
        var eq = setCookieValue.IndexOf('=');
        if (eq <= 0)
            return false;

        var cookieName = setCookieValue.AsSpan(0, eq).Trim();

        foreach (var known in BotChallengeCookieNames)
            if (cookieName.Equals(known, StringComparison.OrdinalIgnoreCase))
                return true;

        foreach (var prefix in BotChallengeCookiePrefixes)
            if (cookieName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    ///     Top-level document navigation (seamless meta-refresh candidate).
    ///     Prefers <c>Sec-Fetch-Dest: document</c>; otherwise Accept prefers text/html.
    /// </summary>
    internal static bool IsDocumentNavigation(Request request)
    {
        var dest = request.Headers.GetFirstHeader("Sec-Fetch-Dest");
        if (dest != null)
            return dest.Value.Equals("document", StringComparison.OrdinalIgnoreCase);

        var accept = request.Headers.GetHeaderValueOrNull(KnownHeaders.Accept);
        if (string.IsNullOrEmpty(accept))
            return false;

        // First Accept token prefers HTML (Chrome document navigations lead with text/html).
        var comma = accept.IndexOf(',');
        var first = (comma >= 0 ? accept.AsSpan(0, comma) : accept.AsSpan()).Trim();
        var semi = first.IndexOf(';');
        if (semi >= 0)
            first = first.Slice(0, semi).Trim();

        return first.Equals("text/html", StringComparison.OrdinalIgnoreCase)
               || first.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
               || first.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Meta-refresh targets must be absolute http(s) URLs (never javascript: / data:).
    /// </summary>
    internal static bool IsSafeMetaRefreshUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    internal static string? ResolveSessionHost(SessionEventArgs args)
    {
        var host = args.HttpClient.Request.RequestUri?.Host;
        if (string.IsNullOrEmpty(host))
            host = args.HttpClient.ConnectRequest?.RequestUri?.Host;
        return host;
    }

    /// <summary>
    ///     Minimal interstitial that triggers an immediate navigation retry (new CONNECT).
    /// </summary>
    internal static string BuildMetaRefreshHtml(string absoluteUrl)
    {
        var escaped = System.Net.WebUtility.HtmlEncode(absoluteUrl);
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\">"
               + "<meta http-equiv=\"refresh\" content=\"0;url=" + escaped + "\">"
               + "<title></title></head><body></body></html>";
    }
}