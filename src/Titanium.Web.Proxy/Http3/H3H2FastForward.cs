using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    ///     Pre-encoded QPACK HEADERS from the H3→H1 one-pass origin parse (no <see cref="Response"/>
    ///     header graph). When set, the client write path skips <c>EncodeResponse</c>.
    /// </summary>
    public byte[]? PreencodedQpackHeaders { get; set; }

    /// <summary>Already-buffered tiny/medium body paired with <see cref="PreencodedQpackHeaders"/>.</summary>
    public byte[]? PreencodedBody { get; set; }

    /// <summary>
    ///     Streamed origin body for large/chunked H3→H1 responses when QPACK was pre-encoded.
    /// </summary>
    public Func<Stream, CancellationToken, Task>? PreencodedStreamBodyWriter { get; set; }

    public required ProxyEndPoint ProxyEndPoint { get; init; }
    public IExternalProxy? CustomUpStreamProxy { get; init; }
    public IPEndPoint? UpStreamEndPoint { get; init; }
    public int MaxBufferedBodyBytes { get; init; }

    /// <summary>Origin SNI / :authority host when ForwardHost rewrites the connect target.</summary>
    public string? OriginAuthorityHost { get; init; }
}
