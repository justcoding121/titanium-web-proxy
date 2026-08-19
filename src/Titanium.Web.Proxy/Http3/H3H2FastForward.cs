using System.Net;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Request-only bag for the interception-off H3→H2 reverse fast path.
///     Avoids <see cref="EventArguments.SessionEventArgs" />, <see cref="HttpWebClient" />,
///     null <c>HttpClientStream</c>, and the unused empty <see cref="Response" /> that the
///     full session graph allocates before origin headers arrive (YARP-style: no extra session
///     object on passthrough; keep <see cref="Request" /> for HPACK encode only).
/// </summary>
internal sealed class H3H2FastForward
{
    public required Request Request { get; init; }
    public Response? Response { get; set; }
    public required ProxyEndPoint ProxyEndPoint { get; init; }
    public IExternalProxy? CustomUpStreamProxy { get; init; }
    public IPEndPoint? UpStreamEndPoint { get; init; }
    public int MaxBufferedBodyBytes { get; init; }
}
