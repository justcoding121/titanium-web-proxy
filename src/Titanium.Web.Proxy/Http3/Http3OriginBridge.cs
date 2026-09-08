#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Quic;
using System.Net.Security;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Handles forwarding an already-decoded inbound HTTP/3 request to the origin server, implementing
///     all necessary protocol bridges:
///     <list type="bullet">
///       <item><description>H3→H3: QUIC origin via <see cref="QuicConnectionPool" />.</description></item>
///       <item><description>H3→H2: TCP origin via <c>Http2OriginConnection</c>.</description></item>
///       <item><description>H3→H1.1: TCP origin via the normal HTTP/1.1 server pipeline.</description></item>
///     </list>
///     Protocol selection is delegated entirely to <see cref="ProxyServer.ResolveHttp3Origin" />;
///     callers that have a pre-resolved <see cref="Http3OriginRoute" /> should use the route-based
///     overload to avoid redundant cache/DNS lookups.
/// </summary>
internal static partial class Http3OriginBridge
{
    // ────────────────────────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Forwards the request using a pre-resolved <paramref name="route" /> produced by
    ///     <see cref="ProxyServer.ResolveHttp3Origin" />.  This overload skips internal
    ///     protocol-selection and uses the effective QUIC port (and optional connect host) from the
    ///     route, which may differ from the URI port/host when Alt-Svc or SVCB advertises an
    ///     alternative service.
    /// </summary>
    /// <param name="onInterimResponse">
    ///     Optional callback invoked for each 1xx interim response received from the origin before the
    ///     final response.
    /// </param>
    internal static async Task ForwardAsync(
        SessionEventArgs sessionArgs,
        ProxyServer server,
        Http3OriginRoute route,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<Response, CancellationToken, Task>? onInterimResponse = null,
        Func<QuicStream, CancellationToken, Task>? copyRequestBody = null)
    {
        var request = sessionArgs.HttpClient.Request;
        var sniHost = request.GetOriginHostPort(request.IsHttps ? 443 : 80).Host;

        if (route.UseH3)
        {
            var connectHost = route.QuicHost ?? sniHost;
            var quicPort = route.QuicPort;
            // Transparent reverse / SOCKS fixed-forward: Forced-H3 routes are keyed by the
            // *client* request authority (often 127.0.0.1:<listen>), which is not the QUIC origin.
            // Prefer ForwardHost/ForwardPort when set — same rule as ForwardOverTcpAsync.
            if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint
                {
                    ForwardHost: { Length: > 0 } forwardHost,
                    ForwardPort: { } forwardPort
                })
            {
                connectHost = forwardHost;
                quicPort = forwardPort;
            }

            await ForwardOverQuicAsync(
                sessionArgs, server,
                connectHost, sniHost, quicPort, route.ForcedH3,
                logger, cancellationToken, onInterimResponse, copyRequestBody);
            return;
        }

        // Route resolved to non-H3 (forced Http2/Http11 override, or no H3 capability known).
        if (sessionArgs.UpstreamHttpProtocol == UpstreamHttpProtocol.Http2)
        {
            await ForwardOverHttp2Async(sessionArgs, server, logger, cancellationToken, onInterimResponse);
            return;
        }

        await ForwardOverTcpAsync(sessionArgs, server, cancellationToken, onInterimResponse);
    }

    /// <summary>
    ///     Forwards the request to the origin after resolving the H3 route via
    ///     <see cref="ProxyServer.ResolveHttp3Origin" />.  Use this overload when no pre-resolved
    ///     route is available (e.g. from the inbound H3 request path).
    /// </summary>
    /// <param name="onInterimResponse">
    ///     Optional callback invoked for each 1xx interim response.
    /// </param>
    /// <param name="copyRequestBody">
    ///     Native HTTP/3 only: when the request body was not buffered during BeforeRequest, copies
    ///     remaining client DATA frames onto the origin request stream. Owned by
    ///     <c>Http3RequestStream</c>; not used by H1→H3 / H2→H3 bridges.
    /// </param>
    internal static async Task ForwardAsync(
        SessionEventArgs sessionArgs,
        ProxyServer server,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<Response, CancellationToken, Task>? onInterimResponse = null,
        Func<QuicStream, CancellationToken, Task>? copyRequestBody = null)
    {
        var request = sessionArgs.HttpClient.Request;
        var (host, port) = request.GetOriginHostPort(443);

        // Delegate route resolution to the centralised authority; background SVCB warming is safe
        // here since we are not inside an H2 frame-reading loop.
        var route = server.ResolveHttp3Origin(
            host, port, sessionArgs.UpstreamHttpProtocol, allowDnsProbe: true);

        await ForwardAsync(sessionArgs, server, route, logger, cancellationToken, onInterimResponse,
            copyRequestBody);
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
}
#pragma warning restore CA1416
