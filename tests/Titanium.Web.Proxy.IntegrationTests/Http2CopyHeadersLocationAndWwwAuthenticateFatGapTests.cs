using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.Headers</c> suite: Location on 302 and
///     WWW-Authenticate on 401 reach the H2 client.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyHeadersLocationAndWwwAuthenticateFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<string?> ReadFinalHeaderAsync(Http2RawClient raw, string expectStatus, string headerName)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is not (Http2FrameType.Headers or Http2FrameType.Continuation))
                continue;
            var decoded = raw.Connection.DecodeHeaders(frame.Payload);
            var status = decoded.FirstOrDefault(h => h.Name == ":status").Value;
            if (status != expectStatus)
                continue;
            return decoded.FirstOrDefault(h => h.Name == headerName).Value;
        }

        return null;
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_Location_On302_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var final = conn.EncodeHeaders(
                    [(":status", "302"), ("location", "https://gap.test/next"), ("content-length", "0")], []);
                await conn.WriteHeaderBlockAsync(streamId, final, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/redir")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var location = await ReadFinalHeaderAsync(raw, "302", "location");
        Assert.AreEqual("https://gap.test/next", location);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_WwwAuthenticate_On401_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var final = conn.EncodeHeaders(
                    [
                        (":status", "401"),
                        ("www-authenticate", "Bearer realm=\"gap\""),
                        ("content-length", "12"),
                    ], []);
                await conn.WriteHeaderBlockAsync(streamId, final, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                    Encoding.ASCII.GetBytes("unauthorized"));
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/auth")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var auth = await ReadFinalHeaderAsync(raw, "401", "www-authenticate");
        Assert.AreEqual("Bearer realm=\"gap\"", auth);
    }
}
