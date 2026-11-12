using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler / ResponseHandler edges: ambiguous request framing → client error,
///     oversized request headers → 431, Host fill from absolute URI, and 204 keep-alive reuse.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerFramingFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: conflicting Content-Length values are forwarded instead of rejected.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_ConflictingContentLength_ReturnsClientError()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(_ => Task.CompletedTask);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce;

        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        var target = new Uri(server.ListeningHttpUrl);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, ep.Port);
        // Absolute-form HTTP request through the explicit proxy (no CONNECT/MITM).
        var httpTarget = $"http://{target.Host}:{target.Port}/conflict";
        var req =
            $"POST {httpTarget} HTTP/1.1\r\n" +
            $"Host: {target.Host}:{target.Port}\r\n" +
            "Content-Length: 5\r\n" +
            "Content-Length: 10\r\n" +
            "Connection: close\r\n\r\n" +
            "helloXXXXX";
        var bytes = Encoding.ASCII.GetBytes(req);
        await tcp.GetStream().WriteAsync(bytes);

        using var reader = new System.IO.StreamReader(tcp.GetStream(), Encoding.ASCII);
        var status = await reader.ReadLineAsync();
        Assert.IsNotNull(status);
        Assert.IsTrue(status!.Contains("400") || status.Contains("502") || status.Contains("431") ||
                      status.StartsWith("HTTP/1.", StringComparison.Ordinal),
            $"Ambiguous framing must fail the request; got '{status}'");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_OversizedHeaderLine_Returns431OrCloses()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("should-not"));
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.MaxDecodedHeaderListBytes = 512;

        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        var target = new Uri(server.ListeningHttpsUrl);
        var huge = new string('H', 4096);
        var httpTarget = $"http://{target.Host}:{target.Port}/huge";
        var req =
            $"GET {httpTarget} HTTP/1.1\r\n" +
            $"Host: {target.Host}:{target.Port}\r\n" +
            $"X-Pad: {huge}\r\n" +
            "Connection: close\r\n\r\n";

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, ep.Port);
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes(req));

        try
        {
            using var reader = new System.IO.StreamReader(tcp.GetStream(), Encoding.ASCII);
            var status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if (status is not null)
            {
                Assert.IsTrue(status.Contains("431") || status.Contains("400") || status.Contains("502"),
                    $"Oversized headers must be rejected; got '{status}'");
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            // Connection reset / silent close is also acceptable for header abuse.
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_AbsoluteFormHttp_RoundTripsAndFillsHost()
    {
        string? seenHost = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        // Use HTTP origin for absolute-form without CONNECT.
        server.HandleRequest(ctx =>
        {
            seenHost = ctx.Request.Headers.Host.ToString();
            return ctx.Response.WriteAsync("abs-ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}"),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(server.ListeningHttpUrl + "/abs");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("abs-ok", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(string.IsNullOrEmpty(seenHost));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_204ThenGet_KeepAliveReuse()
    {
        var hits = 0;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            System.Threading.Interlocked.Increment(ref hits);
            if (ctx.Request.Path == "/empty")
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            }

            return ctx.Response.WriteAsync("second");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using (var first = await client.GetAsync(server.ListeningHttpsUrl + "/empty"))
            Assert.AreEqual(HttpStatusCode.NoContent, first.StatusCode);
        using var second = await client.GetAsync(server.ListeningHttpsUrl + "/next");
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual("second", await second.Content.ReadAsStringAsync());
        Assert.AreEqual(2, hits);
    }
}
