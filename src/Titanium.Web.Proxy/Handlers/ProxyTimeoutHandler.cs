using System;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy;

/// <summary>
///     Resolves effective timeout values from server defaults (and session overrides when present).
/// </summary>
public partial class ProxyServer
{
    /// <summary>
    ///     Effective response-header deadline for <paramref name="args" />, or null when disabled /
    ///     exempt (WebSocket, SSE, already-committed client response).
    /// </summary>
    internal TimeSpan? ResolveResponseHeaderTimeout(SessionEventArgs args)
    {
        if (!ShouldApplyResponseHeaderTimeout(args)) return null;
        return SecondsToTimeout(ResponseHeaderTimeoutSeconds);
    }

    /// <summary>
    ///     Effective idle-read window for <paramref name="args" />, or null when disabled.
    /// </summary>
    internal TimeSpan? ResolveIdleReadTimeout(SessionEventArgs args)
    {
        return SecondsToTimeout(IdleReadTimeoutSeconds);
    }

    /// <summary>
    ///     Effective idle-write window for <paramref name="args" />, or null when disabled.
    /// </summary>
    internal TimeSpan? ResolveIdleWriteTimeout(SessionEventArgs args)
    {
        return SecondsToTimeout(IdleWriteTimeoutSeconds);
    }

    /// <summary>
    ///     Effective total request deadline for <paramref name="args" />, or null when disabled.
    /// </summary>
    internal TimeSpan? ResolveRequestTimeout(SessionEventArgs args)
    {
        return SecondsToTimeout(RequestTimeoutSeconds);
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

    private static TimeSpan? SecondsToTimeout(int serverSeconds)
    {
        return serverSeconds > 0 ? TimeSpan.FromSeconds(serverSeconds) : null;
    }
}
