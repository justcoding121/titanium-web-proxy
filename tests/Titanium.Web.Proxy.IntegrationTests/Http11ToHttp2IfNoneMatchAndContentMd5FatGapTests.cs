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
///     Fat <c>Http11ToHttp2BridgeHandler</c> suite: If-None-Match and Content-MD5 reach the H2 origin.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http11ToHttp2IfNoneMatchAndContentMd5FatGapTests
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
    public async Task H1ToH2_IfNoneMatch_ReachesOrigin()
    {
        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seen = headers.FirstOrDefault(h => h.Name == "if-none-match").Value;
                var resp = conn.EncodeHeaders([(":status", "200"), ("content-length", "0")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        var uri = new Uri(origin.Url);
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"bridge-inm\"");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("\"bridge-inm\"", seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_ContentMd5_OnPost_ReachesOrigin()
    {
        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, body) = await conn.ReadRequestAsync();
                seen = headers.FirstOrDefault(h => h.Name == "content-md5").Value;
                var resp = conn.EncodeHeaders([(":status", "200"), ("content-length", "0")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
                Assert.AreEqual("body", Encoding.UTF8.GetString(body));
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        var uri = new Uri(origin.Url);
        using var client = suite.GetClient(proxy);
        using var content = new StringContent("body", Encoding.UTF8, "text/plain");
        content.Headers.ContentMD5 = Convert.FromBase64String("hBotaJrYa9FhFEdFPCLG/A==");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("hBotaJrYa9FhFEdFPCLG/A==", seen);
    }
}
