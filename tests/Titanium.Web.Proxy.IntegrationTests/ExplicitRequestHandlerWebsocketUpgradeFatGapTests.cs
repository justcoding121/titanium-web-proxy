using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler suite: HTTP/1.1 WebSocket upgrade through an explicit MITM listener.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerWebsocketUpgradeFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: explicit proxy drops or rewrites the origin 101 upgrade response.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_WebSocketUpgrade_Relays101()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleTcpRequest(async ctx =>
        {
            await DrainRequestHeadersAsync(ctx);
            var handshake = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "\r\n");
            await ctx.Transport.Output.WriteAsync(handshake);
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
            "GET / HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n");
        await stream.WriteAsync(request);

        var headers = await ReadHeadersAsync(stream, TimeSpan.FromSeconds(10));
        Assert.IsTrue(headers.StartsWith("HTTP/1.1 101", StringComparison.Ordinal), headers);
        StringAssert.Contains(headers, "Upgrade: websocket");
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

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new System.Threading.CancellationTokenSource(timeout);
        var buffer = new byte[4096];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cts.Token);
            if (n == 0) break;
            total += n;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
                return text[..(end + 4)];
        }

        return Encoding.ASCII.GetString(buffer, 0, total);
    }
}
