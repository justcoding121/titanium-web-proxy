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
        // One summary line at Debug (DNS misses and refusals arrive by the hundred under load);
        // the stack is kept at Trace.
        logger.LogDebug("[proxy<->origin] connection setup FAILED for '{Host}:{Port}': {Chain}",
            host, port, Describe(ex));
        if (logger.IsEnabled(LogLevel.Trace))
            logger.LogTrace(ex, "[proxy<->origin] connection setup failure detail for '{Host}:{Port}'", host, port);
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
        var id = string.Equals(reason, "endpoint limit", StringComparison.Ordinal)
            ? LimitId.AdmissionEndpoint
            : LimitId.AdmissionGlobal;
        LimitExceeded(logger, id, Options.PolicyMode.Enforce, 0, reason, "connection rejected",
            $"{endPoint.IpAddress}:{endPoint.Port}");
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
            "Starting with profile {Profile} (body={Body}, decompressionRatio={DecompressionRatio}, headerLimits={HeaderLimits}, admission={Admission}, http2AbuseBudget={Http2AbuseBudget}, http2RelayValidation={Http2RelayValidation}, webSocketFrameBudget={WebSocketFrameBudget}, allowAmbiguousFraming={AllowAmbiguousFraming}).",
            profile,
            policyModes[Options.PolicyFamily.BodyBudget],
            policyModes[Options.PolicyFamily.DecompressionRatio],
            policyModes[Options.PolicyFamily.HeaderLimits],
            policyModes[Options.PolicyFamily.AdmissionControl],
            policyModes[Options.PolicyFamily.Http2AbuseBudget],
            policyModes[Options.PolicyFamily.Http2RelayValidation],
            policyModes[Options.PolicyFamily.WebSocketFrameBudget],
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
        // Enforce rejects a request, so it must be visible at the default Error minimum level.
        var level = mode == Options.PolicyMode.Enforce ? LogLevel.Error : LogLevel.Debug;
        if (!logger.IsEnabled(level)) return;
        logger.Log(level, "Policy family {Family} breached under {Mode}: {Detail}", family, mode, detail);
    }

    /// <summary>
    ///     A configured limit rejected or observed a request. Enforce logs at Error, Observe at Debug.
    ///     The line names the property, the limit, the client-visible action, and the CLI key.
    ///     Logged at most once per 10 seconds per <paramref name="id"/>; metrics count every call.
    ///     Never pass URLs, header values, cookies, or bodies. <paramref name="host"/> is a host name only.
    /// </summary>
    internal static void LimitExceeded(ILogger logger, LimitId id, Options.PolicyMode mode,
        long observed, object limit, string action, string? host = null)
    {
        var entry = LimitCatalog.Get(id);
        if (entry.Family is { } family)
            global::Titanium.Web.Proxy.Diagnostics.ProxyMetrics.PolicyBreach(family, mode);
        global::Titanium.Web.Proxy.Diagnostics.ProxyMetrics.StreamRejected(entry.FamilyKey, LimitCatalog.Reason(id));

        var suppressed = LimitLogThrottle.TryAcquire(id);
        if (suppressed < 0) return;

        var level = mode == Options.PolicyMode.Enforce ? LogLevel.Error : LogLevel.Debug;
        if (!logger.IsEnabled(level)) return;

        if (suppressed > 0)
        {
            logger.Log(level,
                "{Family} ({Mode}): {What} exceeded {Property}={Limit}; observed {Observed}; client got {Action}. {Hint} ({Suppressed} similar events suppressed){HostSuffix}",
                entry.FamilyKey, mode, entry.What, entry.Property, limit, observed, action, entry.Hint,
                suppressed, FormatHostSuffix(host));
            return;
        }

        logger.Log(level,
            "{Family} ({Mode}): {What} exceeded {Property}={Limit}; observed {Observed}; client got {Action}. {Hint}{HostSuffix}",
            entry.FamilyKey, mode, entry.What, entry.Property, limit, observed, action, entry.Hint,
            FormatHostSuffix(host));
    }

    /// <summary>
    ///     Decrypt-bypass learning changed later CONNECTs for this host to opaque tunnels.
    ///     Warning, not Error: the current request was not failed. Host name only.
    /// </summary>
    internal static void DecryptFailureBypassLearned(ILogger logger, string host, string reason = "origin TLS failure")
    {
        if (!logger.IsEnabled(LogLevel.Warning)) return;
        logger.LogWarning(
            "EnableDecryptFailureBypass learned {Host} ({Reason}); later CONNECTs tunnel opaque. Set server.enableDecryptFailureBypass to false to stop learning.",
            host, reason);
    }

    /// <summary>
    ///     A client kept aborting the MITM TLS handshake for this host (pinning / untrusted proxy root),
    ///     so later CONNECTs tunnel opaque. Warning, not Error: the proxy behaved correctly.
    /// </summary>
    internal static void ClientRejectedCertificateBypassLearned(ILogger logger, string host, int failures)
    {
        if (!logger.IsEnabled(LogLevel.Warning)) return;
        logger.LogWarning(
            "Client rejected the proxy certificate for {Host} {Failures} times in a row (certificate pinning or untrusted proxy root); later CONNECTs tunnel opaque. Set server.enableDecryptFailureBypass to false to stop learning.",
            host, failures);
    }

    /// <summary>
    ///     Per-host throttle so one pinning host retrying hundreds of times cannot hide the first abort
    ///     of any other host. Bounded: the table is reset when it grows past <see cref="MaxHosts" />.
    /// </summary>
    internal static class HandshakeAbortThrottle
    {
        internal const int MaxHosts = 512;
        internal static readonly long IntervalTicks = System.Diagnostics.Stopwatch.Frequency * 10;

        private sealed class State
        {
            public long LastTicks;
            public int Suppressed;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, State> hosts =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Returns -1 when suppressed, otherwise the number of aborts suppressed since the last line.</summary>
        internal static int TryAcquire(string host, long nowTicks)
        {
            if (hosts.Count > MaxHosts) hosts.Clear();

            var state = hosts.GetOrAdd(host ?? string.Empty, static _ => new State());
            lock (state)
            {
                if (state.LastTicks != 0 && nowTicks - state.LastTicks < IntervalTicks)
                {
                    state.Suppressed++;
                    return -1;
                }

                state.LastTicks = nowTicks;
                var suppressed = state.Suppressed;
                state.Suppressed = 0;
                return suppressed;
            }
        }

        internal static void Reset() => hosts.Clear();
    }

    /// <summary>
    ///     One-line, throttled (at most once per 10 s per host; the rest are counted) record of an aborted client
    ///     handshake. Replaces a multi-line stack per abort: a pinning client can retry hundreds of times.
    /// </summary>
    internal static void ClientHandshakeAborted(ILogger logger, string host, Exception error)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;

        var suppressed = HandshakeAbortThrottle.TryAcquire(host, System.Diagnostics.Stopwatch.GetTimestamp());
        if (suppressed < 0) return;

        logger.LogDebug(
            "Client TLS handshake aborted for {Host}: {Reason}{Suppressed}",
            host, error.GetBaseException().Message,
            suppressed > 0 ? $" ({suppressed} similar aborts suppressed)" : string.Empty);
    }

    /// <summary>
    ///     Warns when an operator set a limit the runtime still treats as reserved.
    /// </summary>
    internal static void ReservedLimit(ILogger logger, string cliKey, object value, string instead)
    {
        if (!logger.IsEnabled(LogLevel.Warning)) return;
        logger.LogWarning(
            "{CliKey}={Value} is reserved and not enforced. {Instead}",
            cliKey, value, instead);
    }

    private static string FormatHostSuffix(string? host) =>
        string.IsNullOrEmpty(host) ? string.Empty : " host " + host;

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

    internal static void Http2ProbeDeferredForClientAlpn(ILogger logger, string connectTarget)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        logger.LogDebug(
            "[http2 probe] '{Target}': origin probe still in flight; speculating client h2 ALPN so ServerHello is not blocked",
            connectTarget);
    }

    internal static void Http2ProbeDeferredFailed(ILogger logger, string connectTarget, Exception failure)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        logger.LogDebug(failure,
            "[http2 probe] '{Target}': deferred origin probe failed after client ALPN", connectTarget);
    }

    internal static void SvcbDnsUnavailable(ILogger logger, string detail)
    {
        if (!logger.IsEnabled(LogLevel.Warning)) return;
        logger.LogWarning("[svcb] {Detail}", detail);
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
