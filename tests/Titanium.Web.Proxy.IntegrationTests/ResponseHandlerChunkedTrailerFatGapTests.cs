using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat ResponseHandler suite: chunked HTTP/1.1 responses carrying trailing headers.
/// </summary>
[TestClass]
public class ResponseHandlerChunkedTrailerFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: chunked origin response trailers are stripped before the client sees them.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_ChunkedResponse_Trailer_ReachesClient()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleTcpRequest(async ctx =>
        {
            await DrainRequestHeadersAsync(ctx);
            var payload =
                "HTTP/1.1 200 OK\r\n" +
                "Transfer-Encoding: chunked\r\n" +
                "Trailer: X-Trail\r\n" +
                "Connection: close\r\n\r\n" +
                "5\r\n" +
                "hello\r\n" +
                "0\r\n" +
                "X-Trail: gap-trailer\r\n\r\n";
            await ctx.Transport.Output.WriteAsync(Encoding.ASCII.GetBytes(payload));
            ctx.Transport.Output.Complete();
        });

        var proxy = suite.GetReverseProxy();
        proxy.EnableHttp2 = false;
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        await using var stream = tcp.GetStream();
        var request = Encoding.ASCII.GetBytes(
            "GET /trail HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(request);

        var response = await ReadAllAsync(stream, TimeSpan.FromSeconds(10));
        var text = Encoding.ASCII.GetString(response);
        StringAssert.Contains(text, "hello");
        StringAssert.Contains(text, "X-Trail: gap-trailer");
    }

    private static async Task DrainRequestHeadersAsync(Microsoft.AspNetCore.Connections.ConnectionContext context)
    {
        var requestText = string.Empty;
        while (!requestText.Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var result = await context.Transport.Input.ReadAsync();
            foreach (var seg in result.Buffer)
                requestText += Encoding.ASCII.GetString(seg.Span);
            context.Transport.Input.AdvanceTo(result.Buffer.End);
        }
    }

    private static async Task<byte[]> ReadAllAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new System.Threading.CancellationTokenSource(timeout);
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var n = await stream.ReadAsync(buffer, cts.Token);
            if (n <= 0) break;
            ms.Write(buffer, 0, n);
        }

        return ms.ToArray();
    }
}
