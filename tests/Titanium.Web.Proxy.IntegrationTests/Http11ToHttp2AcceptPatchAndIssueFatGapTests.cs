using System;
using System.Linq;
using System.Net;
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
///     Fat <c>Http11ToHttp2BridgeHandler</c> suite: Accept-Patch and Issue reach the H2 origin.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http11ToHttp2AcceptPatchAndIssueFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static ProxyServer H1ToH2(TestSuite suite)
    {
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
        return proxy;
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_AcceptPatch_ReachesOrigin()
    {
        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seen = headers.FirstOrDefault(h => h.Name == "accept-patch").Value;
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        var uri = new Uri(origin.Url);
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Options, uri);
        request.Headers.TryAddWithoutValidation("Accept-Patch", "text/example;charset=utf-8");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.AreEqual("text/example;charset=utf-8", seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_Issue_ReachesOrigin()
    {
        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seen = headers.FirstOrDefault(h => h.Name == "issue").Value;
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        var uri = new Uri(origin.Url);
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Issue", "https://example.net/policy#frag");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.IsTrue(seen?.Contains("example.net", StringComparison.Ordinal), seen);
    }

    /// <summary>
    ///     Bug class: <c>TE: trailers</c> on the H1 request is dropped on the H2 origin leg.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_TeTrailers_ReachesOrigin()
    {
        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seen = headers.FirstOrDefault(h => h.Name == "te").Value;
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        var uri = new Uri(origin.Url);
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("TE", "trailers");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.AreEqual("trailers", seen);
    }
}
