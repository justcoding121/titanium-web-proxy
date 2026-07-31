using System;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy;

/// <summary>
///     Resolves effective per-session timeout values from server defaults and session overrides.
/// </summary>
public partial class ProxyServer
{
    /// <summary>
    ///     Effective client request-line/header deadline, or null when disabled. No per-session override
    ///     exists: this deadline covers the read that happens before any <see cref="SessionEventArgs" />
    ///     for the request exists to override it on.
    /// </summary>
    internal TimeSpan? ResolveClientHeaderTimeout()
    {
        return ClientHeaderTimeoutSeconds > 0 ? TimeSpan.FromSeconds(ClientHeaderTimeoutSeconds) : null;
    }

    /// <summary>
    ///     Effective response-header deadline for <paramref name="args" />, or null when disabled /
    ///     exempt (WebSocket, SSE, already-committed client response).
    /// </summary>
    internal TimeSpan? ResolveResponseHeaderTimeout(SessionEventArgs args)
    {
        if (!ShouldApplyResponseHeaderTimeout(args)) return null;
        return ResolveTimeout(args.ResponseHeaderTimeout, ResponseHeaderTimeoutSeconds);
    }

    /// <summary>
    ///     Effective idle-read window for <paramref name="args" />, or null when disabled.
    /// </summary>
    internal TimeSpan? ResolveIdleReadTimeout(SessionEventArgs args)
    {
        return ResolveTimeout(args.IdleReadTimeout, IdleReadTimeoutSeconds);
    }

    /// <summary>
    ///     Effective idle-write window for <paramref name="args" />, or null when disabled.
    /// </summary>
    internal TimeSpan? ResolveIdleWriteTimeout(SessionEventArgs args)
    {
        return ResolveTimeout(args.IdleWriteTimeout, IdleWriteTimeoutSeconds);
    }

    /// <summary>
    ///     Effective total request deadline for <paramref name="args" />, or null when disabled.
    /// </summary>
    internal TimeSpan? ResolveRequestTimeout(SessionEventArgs args)
    {
        return ResolveTimeout(args.RequestTimeout, RequestTimeoutSeconds);
    }

    /// <summary>
    ///     Response-header deadlines must not cut short WebSockets, SSE, raw tunnels, or sessions that
    ///     already sent a response status to the client. Those waits may still use idle/read timeouts.
    /// </summary>
    internal static bool ShouldApplyResponseHeaderTimeout(SessionEventArgs args)
    {
        if (args.IsClientResponseCommitted) return false;
        if (args.HttpClient.Request.UpgradeToWebSocket) return false;

        var accept = args.HttpClient.Request.Headers.GetHeaderValueOrNull(KnownHeaders.Accept);
        if (accept != null &&
            accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    ///     <paramref name="sessionOverride"/> null → server seconds; non-positive override or seconds → disabled.
    /// </summary>
    private static TimeSpan? ResolveTimeout(TimeSpan? sessionOverride, int serverSeconds)
    {
        if (sessionOverride.HasValue)
            return sessionOverride.Value > TimeSpan.Zero ? sessionOverride.Value : null;

        return serverSeconds > 0 ? TimeSpan.FromSeconds(serverSeconds) : null;
    }
}
