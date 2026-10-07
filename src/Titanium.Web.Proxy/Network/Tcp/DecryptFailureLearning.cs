using System;
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
    ///     MITM HTTPS 403/429 from the origin (not synthetic Ok/Respond/GenericResponse).
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

        return response.StatusCode is 403 or 429;
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