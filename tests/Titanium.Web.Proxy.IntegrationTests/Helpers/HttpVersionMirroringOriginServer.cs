using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     A minimal raw plain-HTTP origin double that mirrors the HTTP version declared on each incoming request's
///     request line back onto its own response status line - the same real-world server behavior (many servers
///     that are only conditionally HTTP/1.1-compliant echo back whatever version the request declared, rather
///     than always answering with the highest version they actually support) that origin-facing HTTP/1.0
///     normalization (<see cref="Models.OriginHttpVersionPolicy" />) exists to work around. Tracks how many
///     separate TCP connections it has accepted, so tests can assert whether a proxy pooled and reused one
///     persistent origin connection across multiple requests or opened a fresh one for each.
/// </summary>
internal sealed class HttpVersionMirroringOriginServer
{
    private static readonly Encoding TextEncoding = HttpHelper.GetEncodingFromContentType(null);

    private int acceptedConnectionCount;

    public int AcceptedConnectionCount => acceptedConnectionCount;

    /// <summary>
    ///     The exact HTTP version string (e.g. "1.0" or "1.1") of the most recently completed request's request
    ///     line, as this double actually saw it - i.e. the origin-facing wire version the proxy chose to send.
    /// </summary>
    public string? LastObservedRequestVersion { get; private set; }

    public async Task HandleRequest(ConnectionContext context)
    {
        Interlocked.Increment(ref acceptedConnectionCount);

        try
        {
            while (true)
            {
                var request = await ReadRequestAsync(context.Transport.Input);
                if (request == null) return; // connection closed by the peer before a full request arrived.

                LastObservedRequestVersion = $"{request.HttpVersion.Major}.{request.HttpVersion.Minor}";

                var keepAlive = ShouldKeepAlive(request);

                var body = TextEncoding.GetBytes("mirror-ok");
                var response = new Response(body)
                {
                    HttpVersion = request.HttpVersion,
                    StatusCode = 200,
                    StatusDescription = "OK"
                };

                if (!keepAlive)
                    response.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionClose.String);
                else if (request.HttpVersion == HttpHeader.Version10)
                    // HTTP/1.0 defaults to non-persistent; an explicit "keep-alive" is required for the proxy's
                    // own Response.KeepAlive to treat this connection as poolable.
                    response.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionKeepAlive.String);

                await context.Transport.Output.WriteAsync(TextEncoding.GetBytes(response.HeaderText));
                await context.Transport.Output.WriteAsync(body);

                if (!keepAlive)
                {
                    context.Transport.Output.Complete();
                    return;
                }
            }
        }
        catch
        {
            // best-effort test double; failures surface via the test's own assertions.
        }
    }

    /// <summary>Mirrors real default-persistence rules for whatever version this request declared.</summary>
    private static bool ShouldKeepAlive(Request request)
    {
        var connectionHeader = request.Headers.GetHeaderValueOrNull(KnownHeaders.Connection);

        if (request.HttpVersion == HttpHeader.Version10)
            return connectionHeader != null &&
                   connectionHeader.EqualsIgnoreCase(KnownHeaders.ConnectionKeepAlive.String);

        return connectionHeader == null || !connectionHeader.EqualsIgnoreCase(KnownHeaders.ConnectionClose.String);
    }

    private static async Task<Request?> ReadRequestAsync(PipeReader input)
    {
        var requestMsg = string.Empty;
        Request? request;
        while ((request = HttpMessageParsing.ParseRequest(requestMsg, false)) == null)
        {
            var result = await input.ReadAsync();
            if (result.Buffer.Length == 0 && result.IsCompleted) return null;

            foreach (var seg in result.Buffer) requestMsg += TextEncoding.GetString(seg.Span);

            input.AdvanceTo(result.Buffer.End);

            if (result.IsCompleted && request == null) return null;
        }

        return request;
    }
}
