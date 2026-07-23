using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Debug-only tracing for the two independent TLS handshakes a decrypting proxy performs per HTTPS
///     tunnel: browser&lt;-&gt;proxy (the proxy impersonates the real origin using a locally-minted leaf
///     certificate) and proxy&lt;-&gt;real origin (the proxy is the real TLS client). Production diagnostics
///     (<c>ProxyServer.ExceptionFunc</c>) only ever see the final, already-wrapped
///     <c>ProxyConnectException</c>/<c>ProxyHttpException</c> for a failed handshake - these traces exist to
///     make the underlying "why" (SNI/host, ALPN offered vs. negotiated, and the exact original exception
///     chain) visible while diagnosing a handshake failure.
///     <para>
///         Every method here is <see cref="ConditionalAttribute">[Conditional("DEBUG")]</see>, so in a
///         Release build the compiler removes both the call itself and the evaluation of its arguments at
///         every call site - identical in cost to <see cref="Debug.WriteLine(string)" /> - rather than a
///         runtime "if (isDebugBuild)" check, which would still pay for argument evaluation (string
///         interpolation, list formatting, etc.) on every call.
///     </para>
///     <para>
///         Output goes to <see cref="Trace.WriteLine(object)" />, which surfaces in Visual Studio's
///         Output/Debug window while attached (or any other registered <see cref="TraceListener" />) without
///         requiring a console.
///     </para>
/// </summary>
internal static class HandshakeDebugLog
{
    [Conditional("DEBUG")]
    internal static void BrowserHandshakeStarting(string connectTarget, SslProtocols offeredProtocols,
        IReadOnlyList<SslApplicationProtocol>? clientAlpn)
    {
        Trace.WriteLine(
            $"[Titanium.Web.Proxy] [browser<->proxy] starting for '{connectTarget}': ssl={offeredProtocols}, client ALPN={FormatAlpn(clientAlpn)}");
    }

    [Conditional("DEBUG")]
    internal static void BrowserHandshakeSucceeded(string connectTarget, SslApplicationProtocol negotiated)
    {
        Trace.WriteLine(
            $"[Titanium.Web.Proxy] [browser<->proxy] succeeded for '{connectTarget}': negotiated={FormatProtocol(negotiated)}");
    }

    [Conditional("DEBUG")]
    internal static void BrowserHandshakeFailed(string connectTarget, Exception ex)
    {
        Trace.WriteLine($"[Titanium.Web.Proxy] [browser<->proxy] FAILED for '{connectTarget}': {Describe(ex)}");
    }

    [Conditional("DEBUG")]
    internal static void OriginHandshakeStarting(string host, int port,
        IReadOnlyList<SslApplicationProtocol>? requestedAlpn)
    {
        Trace.WriteLine(
            $"[Titanium.Web.Proxy] [proxy<->origin] starting for '{host}:{port}': requested ALPN={FormatAlpn(requestedAlpn)}");
    }

    [Conditional("DEBUG")]
    internal static void OriginHandshakeSucceeded(string host, int port, SslApplicationProtocol negotiated)
    {
        Trace.WriteLine(
            $"[Titanium.Web.Proxy] [proxy<->origin] succeeded for '{host}:{port}': negotiated={FormatProtocol(negotiated)}");
    }

    [Conditional("DEBUG")]
    internal static void OriginConnectionFailed(string host, int port, Exception ex)
    {
        // Covers the whole connection setup to the origin (DNS/TCP connect, optional upstream-proxy CONNECT
        // tunnel, and the TLS handshake itself) - if the immediately preceding trace line is
        // OriginHandshakeStarting for the same host:port, the failure happened during the TLS handshake.
        Trace.WriteLine($"[Titanium.Web.Proxy] [proxy<->origin] connection setup FAILED for '{host}:{port}': {Describe(ex)}");
    }

    [Conditional("DEBUG")]
    internal static void Http2ProbeResult(string connectTarget, bool fromCache, bool supported, Exception? failure)
    {
        Trace.WriteLine(failure == null
            ? $"[Titanium.Web.Proxy] [http2 probe] '{connectTarget}' ({(fromCache ? "cached" : "fresh")}): supported={supported}"
            : $"[Titanium.Web.Proxy] [http2 probe] '{connectTarget}' failed, treating as unsupported (not cached): {Describe(failure)}");
    }

    private static string FormatAlpn(IReadOnlyList<SslApplicationProtocol>? alpn)
    {
        if (alpn == null || alpn.Count == 0) return "(none)";
        return string.Join(",", alpn.Select(FormatProtocol));
    }

    private static string FormatProtocol(SslApplicationProtocol protocol)
    {
        if (protocol == SslApplicationProtocol.Http2) return "h2";
        if (protocol == SslApplicationProtocol.Http11) return "http/1.1";
        if (protocol == default) return "(none)";
        return protocol.ToString();
    }

    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        var current = ex;
        while (current != null)
        {
            if (sb.Length > 0) sb.Append(" -> caused by ");
            sb.Append(current.GetType().Name).Append(": ").Append(current.Message);
            current = current.InnerException;
        }

        return sb.ToString();
    }
}
