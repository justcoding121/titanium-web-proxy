using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Result of <see cref="Http3OriginClientSession.OpenExtendedConnectAsync" />. When the origin
///     accepts the extended CONNECT (<see cref="IsEstablished" />), <see cref="Stream" /> is the
///     duplex tunnel; otherwise <see cref="Stream" /> is null and <see cref="Response" /> carries the
///     rejection.
/// </summary>
internal sealed class Http3OriginTunnelResult
{
    internal Http3OriginTunnelResult(Response response, Http3TunnelStream? stream)
    {
        Response = response;
        Stream = stream;
    }

    internal Response Response { get; }
    internal Http3TunnelStream? Stream { get; }
    internal bool IsEstablished => Stream != null && Response.StatusCode is >= 200 and < 300;
}
