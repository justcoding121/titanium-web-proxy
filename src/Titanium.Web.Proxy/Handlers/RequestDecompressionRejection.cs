using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Logging;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     Answers a request whose body failed bounded decompression: 413 when it outgrew a limit, 400 when it is
    ///     not valid gzip / Brotli. Nothing was sent to the origin yet. The client connection is closed because the
    ///     rest of an over-long body may still be on the wire.
    /// </summary>
    private async Task RejectRequestDecompressionAsync(SessionEventArgs args, RequestDecompressionOutcome outcome,
        HttpClientStream clientStream, CancellationToken cancellationToken)
    {
        var status = outcome == RequestDecompressionOutcome.Corrupt
            ? HttpStatusCode.BadRequest
            : HttpStatusCode.RequestEntityTooLarge;
        var limit = RequestDecompressionPolicy;

        ProxyDiagnostics.ReportBenign(logger,
            $"Request body decompression rejected ({outcome}, limit {limit?.MaxDecompressedBytes:N0} bytes, ratio {limit?.MaxRatio}); returning {(int)status}",
            new System.IO.InvalidDataException(outcome.ToString()));

        args.HttpClient.Response = new GenericResponse(status)
        {
            HttpVersion = args.HttpClient.Request.HttpVersion,
        };
        args.HttpClient.Response.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionClose);
        await clientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
        args.IsClientResponseCommitted = true;
    }
}
