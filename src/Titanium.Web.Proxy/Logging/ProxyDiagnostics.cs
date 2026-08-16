using System;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Logging;

/// <summary>
///     The single gateway every part of the proxy goes through to report a caught exception or other
///     diagnostic event - the direct replacement for the removed <c>ExceptionHandler</c>/
///     <c>ProxyServer.ExceptionFunc</c> delegate. Centralizing this here (rather than calling
///     <see cref="ILogger" /> extension methods ad hoc all over the codebase) is what makes the tiered
///     severity policy ("benign vs. unexpected", see the plan's "Exception coverage rule") a single,
///     auditable decision instead of one made independently at every call site.
///     Every method first checks <see cref="ILogger.IsEnabled(LogLevel)" /> before doing any formatting
///     work, so a disabled level (including the entire gateway when logging is turned off, since a
///     disabled <see cref="ILogger" /> reports every level as not enabled) costs a single virtual call.
/// </summary>
internal static class ProxyDiagnostics
{
    private const string ContextTemplate = "{Context}";
    /// <summary>
    ///     Reports a caught exception that is expected/benign under normal operation - client
    ///     disconnects, cancelled operations, expected socket resets, cache races, retries, and similar.
    ///     These are always visible when diagnosing a problem (via <see cref="LogLevel.Debug" />/
    ///     <see cref="LogLevel.Trace" />) but never contribute to <see cref="LogLevel.Error" />-level
    ///     noise in the default configuration.
    /// </summary>
    public static void ReportBenign(ILogger logger, string context, Exception exception)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        logger.LogDebug(exception, ContextTemplate, context);
    }

    /// <summary>
    ///     Debug-only breadcrumb for a catch site that rethrows, wraps, swallows, or otherwise continues
    ///     without making a terminal severity decision. Never writes at <see cref="LogLevel.Error" />;
    ///     use <see cref="ReportException" />, <see cref="ReportBenign" />, or
    ///     <see cref="ReportUnexpected" /> at the site that owns the final outcome. When Debug is
    ///     disabled this is a single <see cref="ILogger.IsEnabled" /> virtual call.
    ///     <para>
    ///         Same Debug sink as <see cref="ReportBenign" />; kept as a distinct API so call sites can
    ///         document intent (intermediate catch vs. classified-benign outcome) without duplicating
    ///         formatting logic.
    ///     </para>
    /// </summary>
    public static void ReportCaught(ILogger logger, string context, Exception exception)
    {
        ReportBenign(logger, context, exception);
    }

    /// <summary>
    ///     Reports very low-level tracing (e.g. a benign event that is not even exception-worthy).
    /// </summary>
    public static void ReportTrace(ILogger logger, string context)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        logger.LogTrace(ContextTemplate, context);
    }

    /// <summary>
    ///     Reports a caught exception whose severity is not immediately obvious from the exception type
    ///     alone; classifies cancellation/disposal as benign and everything else as unexpected. This is
    ///     the default choice for a generic <c>catch (Exception ex)</c> block being migrated off the old
    ///     <c>ExceptionFunc</c> API.
    /// </summary>
    public static void ReportException(ILogger logger, string context, Exception exception)
    {
        if (IsExpected(exception))
        {
            ReportBenign(logger, context, exception);
            return;
        }

        ReportUnexpected(logger, context, exception);
    }

    /// <summary>
    ///     Reports a genuinely unexpected failure (i.e. one that is not a normal part of proxy operation)
    ///     at <see cref="LogLevel.Error" />.
    /// </summary>
    public static void ReportUnexpected(ILogger logger, string context, Exception exception)
    {
        if (!logger.IsEnabled(LogLevel.Error)) return;
        logger.LogError(exception, ContextTemplate, context);
    }

    /// <summary>
    ///     Reports a failure severe enough that the proxy (or one of its core subsystems) cannot continue
    ///     operating normally, at <see cref="LogLevel.Critical" />.
    /// </summary>
    public static void ReportCritical(ILogger logger, string context, Exception? exception = null)
    {
        if (!logger.IsEnabled(LogLevel.Critical)) return;
        if (exception != null)
            logger.LogCritical(exception, ContextTemplate, context);
        else
            logger.LogCritical(ContextTemplate, context);
    }

    /// <summary>
    ///     Reports a condition worth surfacing but that is not itself an exception, e.g. an object being
    ///     finalized without having been disposed. Unconditional (no build-configuration guard) - the
    ///     direct replacement for the removed <c>Helpers/FinalizerGuard.cs</c> <c>Trace.WriteLine</c>
    ///     call, which only ever ran in Debug builds.
    /// </summary>
    public static void ReportWarning(ILogger logger, string context)
    {
        if (!logger.IsEnabled(LogLevel.Warning)) return;
        logger.LogWarning(ContextTemplate, context);
    }

    /// <summary>
    ///     Reports an informational, non-error event (startup/shutdown milestones, etc.).
    /// </summary>
    public static void ReportInformation(ILogger logger, string context)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        logger.LogInformation(ContextTemplate, context);
    }

    /// <summary>
    ///     Reports that an object was finalized without having been disposed first - the unconditional
    ///     replacement for the six <c>#if DEBUG</c>-guarded <c>FinalizerGuard.ReportUndisposedFinalizer</c>
    ///     call sites. Uses the supplied logger if one is available (live per-instance loggers), or a
    ///     process-wide fallback logger for the small number of low-level types (e.g.
    ///     <see cref="StreamExtended.Network.CopyStream" />) that have no owning <see cref="ProxyServer" />
    ///     reference to source a logger from.
    /// </summary>
    public static void ReportUndisposedFinalizer(ILogger? logger, string typeName)
    {
        var effectiveLogger = logger ?? Logger;
        ReportWarning(effectiveLogger, $"{typeName} was finalized without being disposed first.");
    }

    /// <summary>
    ///     A minimal always-available logger used only by the handful of low-level types that have no
    ///     path to an owning <see cref="ProxyServer" />'s live logger. Defaults to a no-op logger; set by
    ///     <see cref="ProxyServer" /> to the most recently created instance's logger so undisposed-object
    ///     warnings are still visible by default without requiring extra plumbing through every stream
    ///     helper class.
    /// </summary>
    internal static ILogger Logger
    {
        get => logger;
        set => logger = value ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    private static ILogger logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    ///     True when <paramref name="exception" /> (or a nested inner exception) is a normal part of
    ///     proxy operation: cancellation (including user <c>TerminateSession</c>), client/server
    ///     disconnects, aborted TLS handshakes, retries after a stale pooled connection, and
    ///     configured timeouts.
    /// </summary>
    internal static bool IsExpected(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => true,
            ObjectDisposedException => true,
            System.IO.IOException => true,
            System.Net.Sockets.SocketException => true,
            // Browser/OS clients routinely abort MITM handshakes (speculative CONNECT, idle tab
            // teardown, cert cache races). SslStream surfaces those as AuthenticationException,
            // often with no IOException inner — so they must be matched here, not only via
            // recursive IOException checks. Caught as ProxyConnectException in Explicit/Transparent
            // handlers and previously logged as red Error after a browsing pause.
            System.Security.Authentication.AuthenticationException => true,
            Exceptions.ProxyTimeoutException => true,
            Exceptions.RetryableServerConnectionException => true,
            _ => exception.InnerException != null && IsExpected(exception.InnerException)
        };
    }
}
