using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Phase 0A characterization tests documenting the CURRENT (pre-Phase-1) handling of HTTP/1.1 chunked
///     trailers. Today the proxy relays a syntactically valid, trailer-less terminator ("0\r\n\r\n") to the
///     client regardless of whether the upstream response actually carried trailers, so trailer headers are
///     silently dropped rather than forwarded. See HttpStream.CopyBodyChunkedAsync, which writes the zero-chunk
///     terminator to the client before reading (and only partially consumes) any trailer lines from the source.
///     Update these assertions once Phase 1 adds RequestResponseBase.TrailingHeaders and forwards trailers.
/// </summary>
[TestClass]
public class ChunkedTrailerCharacterizationTests
{
    private static readonly Encoding MsgEncoding = HttpHelper.GetEncodingFromContentType(null);

    [TestMethod]
    public async Task Chunked_Response_Body_Is_Relayed_But_Trailer_Header_Is_Currently_Dropped()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            // Drain the (headers-only, bodyless GET) request so the write below completes cleanly.
            var requestText = string.Empty;
            while (!requestText.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var result = await context.Transport.Input.ReadAsync();
                foreach (var seg in result.Buffer)
                {
                    requestText += MsgEncoding.GetString(seg.Span);
                }

                context.Transport.Input.AdvanceTo(result.Buffer.End);
            }

            // A chunked response whose terminating zero-chunk is followed by a trailer header.
            var response = MsgEncoding.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Transfer-Encoding: chunked\r\n" +
                "\r\n" +
                "5\r\nhello\r\n" +
                "0\r\n" +
                "X-Trailer: trailer-value\r\n" +
                "\r\n");

            await context.Transport.Output.WriteAsync(response);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.GetAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("hello", body, "The chunked body content itself is still relayed correctly.");

        // Documents today's gap: the trailer header never reaches the client.
        Assert.IsFalse(response.Headers.Contains("X-Trailer"));
        Assert.IsFalse(response.TrailingHeaders.Contains("X-Trailer"));
    }
}
