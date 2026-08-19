using System.Net;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Request-only bag for interception-off H3→origin reverse fast paths (H3→H2 / H3→H3 / H3→H1).
///     Avoids <see cref="EventArguments.SessionEventArgs" />, <see cref="HttpWebClient" />,
///     null <c>HttpClientStream</c>, and the unused empty <see cref="Response" /> that the
///     full session graph allocates before origin headers arrive (YARP-style: no extra session
///     object on passthrough; keep <see cref="Request" /> for HPACK/QPACK encode only).
/// </summary>
internal sealed class H3H2FastForward
{
    public required Request Request { get; init; }
    public Response? Response { get; set; }
    public required ProxyEndPoint ProxyEndPoint { get; init; }
    public IExternalProxy? CustomUpStreamProxy { get; init; }
    public IPEndPoint? UpStreamEndPoint { get; init; }
    public int MaxBufferedBodyBytes { get; init; }

    /// <summary>Origin SNI / :authority host when ForwardHost rewrites the connect target.</summary>
    public string? OriginAuthorityHost { get; init; }
}
