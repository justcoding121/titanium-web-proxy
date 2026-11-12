using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class Http2CopyAccessControlAllowOriginWave43FatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_Acao_On200_ReachesH2Client()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var block = conn.EncodeHeaders(
                    [
                        (":status", "200"),
                        ("access-control-allow-origin", "https://app.example"),
                        ("content-length", "0"),
                    ], []);
                await conn.WriteHeaderBlockAsync(streamId, block, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.WriteHeaderBlockAsync(1,
            raw.Connection.EncodeHeaders(
                [(":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/acao")],
                []),
            endStream: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is not (Http2FrameType.Headers or Http2FrameType.Continuation)) continue;
            var decoded = raw.Connection.DecodeHeaders(frame.Payload);
            if (decoded.FirstOrDefault(h => h.Name == ":status").Value is not "200") continue;
            Assert.AreEqual("https://app.example",
                decoded.FirstOrDefault(h => h.Name == "access-control-allow-origin").Value);
            return;
        }

        Assert.Fail("No response headers.");
    }
}
