using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

[DoNotParallelize]
[TestClass]
public class Http2ToHttp11LargeBodyWave42FatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(60_000)]
    public async Task H2ToH1_Origin64kBody_RoundTrips()
    {
        const string marker = "wave42-h2-h1-body-";
        var payload = marker + new string('b', 16 * 1024);
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync(payload));
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);
        await raw.Connection.WriteHeaderBlockAsync(1,
            raw.Connection.EncodeHeaders(
                [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/big")], []),
            endStream: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        using var ms = new System.IO.MemoryStream();
        while (DateTime.UtcNow < deadline && ms.Length < payload.Length)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.Data)
                ms.Write(frame.Payload, 0, frame.Payload.Length);
            if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                break;
        }

        Assert.IsTrue(Encoding.UTF8.GetString(ms.ToArray()).StartsWith(marker, StringComparison.Ordinal));
        Assert.AreEqual(payload.Length, ms.Length);
    }
}
