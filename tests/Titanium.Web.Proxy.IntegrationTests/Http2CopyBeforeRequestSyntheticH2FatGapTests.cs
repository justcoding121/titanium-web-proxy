using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> MITM synthetic arms: BeforeRequest <c>Ok</c> on a native
///     H2 client must suppress the origin round trip and keep the connection usable.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyBeforeRequestSyntheticH2FatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: BeforeRequest Ok still forwards the request to the H2 origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2_BeforeRequestOk_SkipsOrigin()
    {
        var contacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                _ = await conn.ReadRequestAsync();
                contacted = true;
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("synthetic-req");
            return Task.CompletedTask;
        };

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", uri.Authority), (":path", "/")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var body = await ReadDataOnStreamAsync(raw, 1, TimeSpan.FromSeconds(12));
        Assert.AreEqual("synthetic-req", Encoding.ASCII.GetString(body));
        Assert.IsFalse(contacted);
    }

    /// <summary>
    ///     Bug class: synthetic BeforeRequest response wedges a follow-up GET on the same connection.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2_BeforeRequestOk_SecondGetStillWorks()
    {
        var exchanges = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                Interlocked.Increment(ref exchanges);
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
        proxy.BeforeRequest += (_, e) =>
        {
            if (e.HttpClient.Request.Url.Contains("syn", StringComparison.Ordinal))
            {
                e.Ok("short");
                return Task.CompletedTask;
            }

            return Task.CompletedTask;
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
        Assert.IsTrue(exchanges >= 1);
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
