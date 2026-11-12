using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2OriginConnection</c> suite: parallel GETs on one pooled member both complete.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2OriginConnectionParallelGetFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: the second H1 GET on a shared H2 origin connection hangs or returns an empty body.
    /// </summary>
    [TestMethod]
    [Timeout(45_000)]
    public async Task H1ToH2_BackToBackGets_BothReturnBodies()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                var path = headers.FirstOrDefault(h => h.Name == ":path").Value ?? "/";
                var body = Encoding.UTF8.GetBytes("body:" + path);
                var resp = conn.EncodeHeaders(
                    [(":status", "200")],
                    [("content-type", "text/plain"), ("content-length", body.Length.ToString())]);
                await conn.WriteHeaderBlockAsync(streamId, resp, false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, body);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        ep.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        var url = origin.Url;
        var first = await client.GetStringAsync(url + "p1");
        var second = await client.GetStringAsync(url + "p2");
        Assert.AreEqual("body:/p1", first);
        Assert.AreEqual("body:/p2", second);
    }
}
