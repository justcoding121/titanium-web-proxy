using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Logging;

/// <summary>
///     The single formatting/message catalog used by every built-in sink, and by the TLS/HTTP-2-probe
///     trace events that replace the removed, Debug-only <c>Helpers/HandshakeDebugLog.cs</c>. Keeping
///     formatting here (rather than duplicated in each provider) is what makes the log line layout "one
///     place" in the codebase.
///     Unlike the old <c>[Conditional("DEBUG")]</c> implementation, every method here is unconditional:
///     it is available in Release builds too whenever <see cref="ProxyLoggingOptions.MinimumLevel" /> is
///     set to <see cref="LogLevel.Trace" />, and costs nothing beyond a single
///     <see cref="ILogger.IsEnabled(LogLevel)" /> check when Trace is not enabled.
/// </summary>
internal static class ProxyLog
{
    /// <summary>
    ///     Renders a single <see cref="LogEntry" /> as one (or, with an exception, several) plain-text
    ///     line(s) suitable for both the console and file sinks.
    /// </summary>
    public static string FormatLine(in LogEntry entry)
    {
        var levelText = LevelToString(entry.Level).PadRight(5);
        var line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{levelText}] {entry.Category}: {entry.Message}";

        if (entry.Exception != null) line += Environment.NewLine + entry.Exception;

        return line;
    }

    private static string LevelToString(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT",
            _ => level.ToString().ToUpperInvariant()
        };
    }

    // --- TLS/HTTP-2-probe tracing (replaces the removed Helpers/HandshakeDebugLog.cs) ---
    // Production diagnostics via ProxyDiagnostics only ever see the final, already-wrapped
    // ProxyConnectException/ProxyHttpException for a failed handshake - these traces exist to make the
    // underlying "why" (SNI/host, ALPN offered vs. negotiated, and the exact original exception chain)
    // visible while diagnosing a handshake failure, without paying for it unless Trace is enabled.

    internal static void BrowserHandshakeStarting(ILogger logger, string connectTarget, SslProtocols offeredProtocols,
        IReadOnlyList<SslApplicationProtocol>? clientAlpn)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        logger.LogTrace("[browser<->proxy] starting for '{Target}': ssl={Ssl}, client ALPN={Alpn}",
            connectTarget, offeredProtocols, FormatAlpn(clientAlpn));
    }

    internal static void BrowserHandshakeSucceeded(ILogger logger, string connectTarget,
        SslApplicationProtocol negotiated)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        logger.LogTrace("[browser<->proxy] succeeded for '{Target}': negotiated={Protocol}",
            connectTarget, FormatProtocol(negotiated));
    }

    internal static void BrowserHandshakeFailed(ILogger logger, string connectTarget, Exception ex)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        logger.LogTrace("[browser<->proxy] FAILED for '{Target}': {Chain}", connectTarget, Describe(ex));
    }

    internal static void OriginHandshakeStarting(ILogger logger, string host, int port,
        IReadOnlyList<SslApplicationProtocol>? requestedAlpn)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        logger.LogTrace("[proxy<->origin] starting for '{Host}:{Port}': requested ALPN={Alpn}",
            host, port, FormatAlpn(requestedAlpn));
    }

    internal static void OriginHandshakeSucceeded(ILogger logger, string host, int port,
        SslApplicationProtocol negotiated)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        logger.LogTrace("[proxy<->origin] succeeded for '{Host}:{Port}': negotiated={Protocol}",
            host, port, FormatProtocol(negotiated));
    }

    internal static void OriginConnectionFailed(ILogger logger, string host, int port, Exception ex)
    {
        // Covers the whole connection setup to the origin (DNS/TCP connect, optional upstream-proxy
        // CONNECT tunnel, and the TLS handshake itself); this is Debug rather than Error because the
        // caller always also throws/propagates the failure, which is reported at its own boundary.
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        logger.LogDebug(ex, "[proxy<->origin] connection setup FAILED for '{Host}:{Port}': {Chain}",
            host, port, Describe(ex));
    }

    /// <summary>
    ///     A client connection was rejected by the admission gate in <c>ProxyServer.OnAcceptConnection</c>.
    ///     Tagged by <paramref name="reason" /> (one of a small fixed set: "global limit"/"endpoint
    ///     limit") and by the endpoint's own <c>ip:port</c>, both naturally bounded label spaces, so this
    ///     stays cardinality-safe however many endpoints a host application creates.
    /// </summary>
    internal static void ClientConnectionAdmissionRejected(ILogger logger, Models.ProxyEndPoint endPoint,
        string reason)
    {
        if (!logger.IsEnabled(LogLevel.Warning)) return;
        logger.LogWarning("Rejected a client connection on {Endpoint} ({Reason}).",
            $"{endPoint.IpAddress}:{endPoint.Port}", reason);
    }

    /// <summary>
    ///     Logs the effective profile and per-family policy modes once per <c>Start()</c> call, per
    ///     the plan's rollout section: name only, never hosts, URLs or secrets.
    /// </summary>
    internal static void EffectiveProfileAtStartup(ILogger logger, Options.ProxyProfile profile,
        Options.ProxyPolicyModes policyModes)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        logger.LogInformation(
            "Starting with profile {Profile} (body={Body}, decompressionRatio={DecompressionRatio}, headerLimits={HeaderLimits}, admission={Admission}, http2AbuseBudget={Http2AbuseBudget}, allowAmbiguousFraming={AllowAmbiguousFraming}).",
            profile,
            policyModes[Options.PolicyFamily.BodyBudget],
            policyModes[Options.PolicyFamily.DecompressionRatio],
            policyModes[Options.PolicyFamily.HeaderLimits],
            policyModes[Options.PolicyFamily.AdmissionControl],
            policyModes[Options.PolicyFamily.Http2AbuseBudget],
            policyModes.AllowAmbiguousFraming);
    }

    /// <summary>
    ///     A resource-bound policy family's limit was breached. Logged at Warning when the breach was
    ///     enforced (rejected/closed/reset) and at Debug when only observed, so an
    ///     <see cref="Options.PolicyMode.Observe" /> deployment measuring what a stricter profile would
    ///     catch does not produce Warning-level noise for every hit.
    /// </summary>
    internal static void PolicyBreach(ILogger logger, Options.PolicyFamily family, Options.PolicyMode mode,
        string detail)
    {
        var level = mode == Options.PolicyMode.Enforce ? LogLevel.Warning : LogLevel.Debug;
        if (!logger.IsEnabled(level)) return;
        logger.Log(level, "Policy family {Family} breached under {Mode}: {Detail}", family, mode, detail);
    }

    internal static void Http2ProbeResult(ILogger logger, string connectTarget, bool fromCache, bool supported,
        Exception? failure)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;

        if (failure == null)
            logger.LogTrace("[http2 probe] '{Target}' ({Source}): supported={Supported}",
                connectTarget, fromCache ? "cached" : "fresh", supported);
        else
            logger.LogTrace("[http2 probe] '{Target}' failed, treating as unsupported (not cached): {Chain}",
                connectTarget, Describe(failure));
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
