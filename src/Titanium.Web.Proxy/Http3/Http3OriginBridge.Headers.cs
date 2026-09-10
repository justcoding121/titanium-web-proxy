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
    private static bool ResponseMayHaveBody(
        int statusCode, string method, long contentLength, bool isChunked, bool connectionClose)
    {
        if (statusCode is >= 100 and < 200) return false;
        if (statusCode is 204 or 304) return false;
        if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)) return false;
        if (contentLength == 0) return false;
        if (contentLength > 0) return true;
        if (isChunked || connectionClose) return true;
        return false;
    }

    private static void PrepareH2OriginRequestHeaders(Request request)
    {
        if (request.Authority.Length == 0)
        {
            var hostHeader = request.Host;
            if (!string.IsNullOrEmpty(hostHeader))
                request.Authority = hostHeader.GetByteString();
        }

        request.Headers.RemoveHeader(KnownHeaders.Connection);
        request.Headers.RemoveHeader("Keep-Alive");
        request.Headers.RemoveHeader(KnownHeaders.ProxyConnection);
        request.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
        request.Headers.RemoveHeader(KnownHeaders.Upgrade);
        request.Headers.RemoveHeader("TE");
        request.Headers.RemoveHeader(KnownHeaders.Host);

        // Fast path when names are already lowercase (QPACK); otherwise rename in place.
        if (request.Headers.Any(h =>
            {
                for (var i = 0; i < h.Name.Length; i++)
                {
                    var c = h.Name[i];
                    if (c is >= 'A' and <= 'Z') return true;
                }

                return false;
            }))
        {
            var renamed = request.Headers
                .Select(h => (Name: h.Name.ToLowerInvariant(), h.Value))
                .ToList();
            request.Headers.Clear();
            foreach (var (name, value) in renamed)
                request.Headers.AddHeader(name, value);
        }

        request.HeaderNamesAreHttp2Normalized = true;
    }

    /// <summary>
    ///     Frame types that RFC 9114 forbids on request streams (must not be silently ignored).
    /// </summary>
    private static bool IsForbiddenOnRequestStream(ulong frameType) =>
        frameType is Http3FrameType.Settings or Http3FrameType.GoAway
            or Http3FrameType.MaxPushId or Http3FrameType.CancelPush;
}
