using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     Selects a local bind endpoint for an upstream socket after the destination IP
///     (and thus address family) is known. Family-specific endpoints win over the legacy
///     single <c>UpStreamEndPoint</c>; a legacy endpoint of the wrong family is ignored
///     so dual-stack destinations can fall back to the OS default bind.
/// </summary>
internal static class UpStreamEndPointSelector
{
    /// <summary>
    ///     Resolves the local bind endpoint for <paramref name="destinationFamily" />.
    ///     Precedence: session family-specific → server family-specific → session generic
    ///     (matching family) → server generic (matching family) → <see langword="null" />.
    /// </summary>
    internal static IPEndPoint? Resolve(AddressFamily destinationFamily,
        IPEndPoint? sessionEndPoint, IPEndPoint? sessionIPv4, IPEndPoint? sessionIPv6,
        IPEndPoint? serverEndPoint, IPEndPoint? serverIPv4, IPEndPoint? serverIPv6)
    {
        var familySpecific = destinationFamily == AddressFamily.InterNetwork
            ? sessionIPv4 ?? serverIPv4
            : destinationFamily == AddressFamily.InterNetworkV6
                ? sessionIPv6 ?? serverIPv6
                : null;

        if (familySpecific != null) return familySpecific;

        var generic = sessionEndPoint ?? serverEndPoint;
        if (generic != null && generic.AddressFamily == destinationFamily)
            return generic;

        return null;
    }

    /// <summary>
    ///     Appends configured bind endpoints to a connection cache key so IPv4-bound and
    ///     IPv6-bound connections never share a pool bucket.
    /// </summary>
    internal static void AppendToCacheKey(StringBuilder cacheKeyBuilder,
        IPEndPoint? upStreamEndPoint, IPEndPoint? upStreamEndPointIPv4, IPEndPoint? upStreamEndPointIPv6)
    {
        AppendOne(cacheKeyBuilder, "g", upStreamEndPoint);
        AppendOne(cacheKeyBuilder, "4", upStreamEndPointIPv4);
        AppendOne(cacheKeyBuilder, "6", upStreamEndPointIPv6);
    }

    private static void AppendOne(StringBuilder cacheKeyBuilder, string tag, IPEndPoint? endPoint)
    {
        if (endPoint == null) return;
        cacheKeyBuilder.Append('-');
        cacheKeyBuilder.Append(tag);
        cacheKeyBuilder.Append(':');
        cacheKeyBuilder.Append(endPoint.Address);
        cacheKeyBuilder.Append(':');
        cacheKeyBuilder.Append(endPoint.Port);
    }
}
