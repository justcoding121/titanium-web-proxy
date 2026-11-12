using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2ToHttp11BridgeHandler</c> suite: Content-Location and Warning
///     on origin H1 responses reach the H2 client.
/// </summary>
[TestClass]
public class Http2ToHttp11ContentLocationAndWarningFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static (ProxyServer Proxy, ExplicitProxyEndPoint Ep) H2ToH1(TestSuite suite)
    {
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        ep.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        return (proxy, ep);
    }

    private static async Task<string?> ReadHeaderAsync(Http2RawClient raw, string headerName)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is not (Http2FrameType.Headers or Http2FrameType.Continuation))
                continue;
            var decoded = raw.Connection.DecodeHeaders(frame.Payload);
            if (decoded.FirstOrDefault(h => h.Name == ":status").Value is not "200")
                continue;
            return decoded.FirstOrDefault(h => h.Name == headerName).Value;
        }

        return null;
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_ContentLocation_ReachesClient()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers.ContentLocation = "/alt/gap";
            return ctx.Response.WriteAsync("ok");
        });
        var (_, ep) = H2ToH1(suite);

        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/cloc")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var cloc = await ReadHeaderAsync(raw, "content-location");
        Assert.AreEqual("/alt/gap", cloc);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_Warning_ReachesClient()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers.Warning = "199 gap \"misc\"";
            return ctx.Response.WriteAsync("warn");
        });
        var (_, ep) = H2ToH1(suite);

        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/warn")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var warn = await ReadHeaderAsync(raw, "warning");
        Assert.AreEqual("199 gap \"misc\"", warn);
    }
}
