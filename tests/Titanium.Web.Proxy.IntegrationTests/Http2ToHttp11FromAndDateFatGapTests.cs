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
///     Fat <c>Http2ToHttp11BridgeHandler</c> suite: Date and From on H1 responses reach the H2 client.
/// </summary>
[TestClass]
public class Http2ToHttp11FromAndDateFatGapTests
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

    /// <summary>
    ///     Bug class: origin Date header is stripped on the H2→H1 bridge response.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_Date_ReachesClient()
    {
        const string marker = "Tue, 09 Nov 2027 12:00:00 GMT";
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers.Date = marker;
            return ctx.Response.WriteAsync("dated");
        });
        var (_, ep) = H2ToH1(suite);

        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/date-gap")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var date = await ReadHeaderAsync(raw, "date");
        Assert.IsNotNull(date);
        Assert.IsTrue(date!.Contains("2027", StringComparison.Ordinal), date);
    }

    /// <summary>
    ///     Bug class: origin From header is stripped on the H2→H1 bridge response.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_From_ReachesClient()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers.From = "gateway@gap.test";
            return ctx.Response.WriteAsync("fromd");
        });
        var (_, ep) = H2ToH1(suite);

        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/from-gap")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var from = await ReadHeaderAsync(raw, "from");
        Assert.AreEqual("gateway@gap.test", from);
    }
}
