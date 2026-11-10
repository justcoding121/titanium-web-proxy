using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2OriginConnection</c> suite: origin H2 response trailers reach the HTTP/1.1 client.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2OriginConnectionResponseTrailersFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: H2 origin trailing headers are stripped on the H1 bridge.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginTrailers_ReachClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders(
                [(":status", "200")],
                [("content-type", "text/plain"), ("content-length", "5")]);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, 0, "hello"u8.ToArray());
            var trailers = conn.EncodeHeaders([], [("x-gap-trail", "seen")]);
            await conn.WriteHeaderBlockAsync(streamId, trailers, endStream: true);
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
        using var response = await client.GetAsync(origin.Url + "trail");
        response.EnsureSuccessStatusCode();
        Assert.AreEqual("hello", await response.Content.ReadAsStringAsync());
        var trail = response.TrailingHeaders.TryGetValues("X-Gap-Trail", out var values)
            ? values.FirstOrDefault()
            : null;
        Assert.AreEqual("seen", trail);
    }
}
