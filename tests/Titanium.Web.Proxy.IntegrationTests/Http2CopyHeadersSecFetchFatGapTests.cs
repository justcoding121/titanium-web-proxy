using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.Headers</c> suite: Sec-Fetch-* request fields reach the H2 origin.
/// </summary>
[TestClass]
public class Http2CopyHeadersSecFetchFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<string?> ReadStatusAsync(Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
            {
                foreach (var h in raw.Connection.DecodeHeaders(frame.Payload))
                {
                    if (h.Name == ":status")
                        return h.Value;
                }
            }
        }

        return null;
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH2_SecFetch_RequestHeadersReachOrigin()
    {
        string? site = null;
        string? mode = null;
        string? dest = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                site = headers.FirstOrDefault(h => h.Name == "sec-fetch-site").Value;
                mode = headers.FirstOrDefault(h => h.Name == "sec-fetch-mode").Value;
                dest = headers.FirstOrDefault(h => h.Name == "sec-fetch-dest").Value;
                var resp = conn.EncodeHeaders([(":status", "200")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
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

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", uri.Authority), (":path", "/")],
            [
                ("sec-fetch-site", "same-origin"),
                ("sec-fetch-mode", "navigate"),
                ("sec-fetch-dest", "document"),
            ]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        Assert.AreEqual("200", await ReadStatusAsync(raw, TimeSpan.FromSeconds(12)));
        Assert.AreEqual("same-origin", site);
        Assert.AreEqual("navigate", mode);
        Assert.AreEqual("document", dest);
    }
}
