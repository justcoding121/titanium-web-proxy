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
///     Fat <c>Http2Helper.Copy.Headers</c> suite: Vary, ETag, and Last-Modified
///     on final 200 responses reach the H2 client.
/// </summary>
[TestClass]
public class Http2CopyHeadersVaryEtagAndModifiedFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<string?> ReadFinalHeaderAsync(Http2RawClient raw, string headerName)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is not (Http2FrameType.Headers or Http2FrameType.Continuation))
                continue;
            var decoded = raw.Connection.DecodeHeaders(frame.Payload);
            var status = decoded.FirstOrDefault(h => h.Name == ":status").Value;
            if (status is not "200")
                continue;
            return decoded.FirstOrDefault(h => h.Name == headerName).Value;
        }

        return null;
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_Vary_On200_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var final = conn.EncodeHeaders(
                    [(":status", "200"), ("vary", "Accept-Encoding"), ("content-length", "1")], []);
                await conn.WriteHeaderBlockAsync(streamId, final, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, [(byte)'v']);
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
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/vary")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var vary = await ReadFinalHeaderAsync(raw, "vary");
        Assert.AreEqual("Accept-Encoding", vary);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_Etag_On200_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var final = conn.EncodeHeaders(
                    [(":status", "200"), ("etag", "\"gap-etag\""), ("content-length", "2")], []);
                await conn.WriteHeaderBlockAsync(streamId, final, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                    Encoding.ASCII.GetBytes("ok"));
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
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/etag")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var etag = await ReadFinalHeaderAsync(raw, "etag");
        Assert.AreEqual("\"gap-etag\"", etag);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_LastModified_On200_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        const string stamp = "Mon, 09 Nov 2026 12:00:00 GMT";
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var final = conn.EncodeHeaders(
                    [(":status", "200"), ("last-modified", stamp), ("content-length", "0")], []);
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
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/lm")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var lm = await ReadFinalHeaderAsync(raw, "last-modified");
        Assert.AreEqual(stamp, lm);
    }
}
