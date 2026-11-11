using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http11ToHttp2BridgeHandler</c> suite: BeforeRequest removes a header,
///     Accept-Encoding inject, and origin 304.
/// </summary>
[TestClass]
public class Http11ToHttp2RemoveHeaderAnd304FatGapTests
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
    public async Task H1ToH2_BeforeRequest_RemovesCustomHeader()
    {
        string? seen = "still-there";
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seen = headers.FirstOrDefault(h => h.Name == "x-drop-me").Value;
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Headers.RemoveHeader("x-drop-me");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url + "drop");
        request.Headers.TryAddWithoutValidation("x-drop-me", "1");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.IsTrue(string.IsNullOrEmpty(seen));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeRequest_InjectsAcceptEncoding()
    {
        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seen = headers.FirstOrDefault(h => h.Name == "accept-encoding").Value;
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Headers.RemoveHeader("Accept-Encoding");
            e.HttpClient.Request.Headers.AddHeader("accept-encoding", "gzip");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "ae");
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.IsTrue(seen!.Contains("gzip", StringComparison.Ordinal), $"got '{seen}'");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_Origin304_SurfacesNotModified()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders([(":status", "304")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "nm");
        Assert.AreEqual(HttpStatusCode.NotModified, response.StatusCode);
        Assert.AreEqual(0, (await response.Content.ReadAsByteArrayAsync()).Length);
    }
}
