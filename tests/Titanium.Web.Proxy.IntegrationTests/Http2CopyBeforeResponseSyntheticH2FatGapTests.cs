using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.cs</c> MITM synthetic arms: BeforeResponse <c>Ok</c> on a native
///     H2 client must suppress relayed origin body frames and leave the origin stream healthy.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyBeforeResponseSyntheticH2FatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: BeforeResponse Ok still streams the origin DATA frames to the client.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2_BeforeResponseOk_ReplacesOriginBody()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var headers = conn.EncodeHeaders([(":status", "200")],
                [("content-type", "text/plain"), ("content-length", "11")]);
            await conn.WriteHeaderBlockAsync(streamId, headers, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("origin-body"));
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        proxy.BeforeResponse += async (_, e) =>
        {
            await e.GetResponseBody();
            e.Ok("synthetic-h2");
        };

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", uri.Authority), (":path", "/")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var body = await ReadDataOnStreamAsync(raw, 1, TimeSpan.FromSeconds(12));
        Assert.AreEqual("synthetic-h2", Encoding.ASCII.GetString(body));
    }

    /// <summary>
    ///     Bug class: synthetic response leaves the H2 exchange wedged for a follow-up GET.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2_BeforeResponseOk_SecondGetStillWorks()
    {
        var exchanges = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                System.Threading.Interlocked.Increment(ref exchanges);
                var headers = conn.EncodeHeaders([(":status", "200")],
                    [("content-length", "2")]);
                await conn.WriteHeaderBlockAsync(streamId, headers, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                    Encoding.ASCII.GetBytes("ok"));
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        proxy.BeforeResponse += async (_, e) =>
        {
            if (e.HttpClient.Request.Url.Contains("syn", StringComparison.Ordinal))
            {
                await e.GetResponseBody();
                e.Ok("short");
                return;
            }

            await e.GetResponseBody();
        };

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var syn = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/syn")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, syn, endStream: true);
        _ = await ReadDataOnStreamAsync(raw, 1, TimeSpan.FromSeconds(10));

        var follow = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/next")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(3, follow, endStream: true);
        var body = await ReadDataOnStreamAsync(raw, 3, TimeSpan.FromSeconds(10));
        Assert.AreEqual("ok", Encoding.ASCII.GetString(body));
        Assert.IsTrue(exchanges >= 2);
    }

    private static async Task<byte[]> ReadDataOnStreamAsync(
        Http2RawClient raw, int expectStream, TimeSpan timeout)
    {
        using var ms = new MemoryStream();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.StreamId != expectStream)
                continue;
            if (frame.Type == Http2FrameType.Data)
            {
                ms.Write(frame.Payload, 0, frame.Payload.Length);
                if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    return ms.ToArray();
            }
        }

        Assert.Fail("Timed out waiting for response DATA.");
        return [];
    }
}
