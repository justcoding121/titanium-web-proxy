using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: declared frame length above MaxAcceptableFrameSize.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyOversizeFrameFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task IdleOriginAsync(Http2RawOriginServer origin)
    {
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });
        await Task.CompletedTask;
    }

    private static async Task<(Http2FrameType Type, int Error)> ReadGoAwayAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.GoAway)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
        }

        Assert.Fail("Timed out waiting for GOAWAY.");
        return default;
    }

    /// <summary>
    ///     Bug class: a DATA frame advertising length &gt; 16 KiB is buffered instead of
    ///     immediate GOAWAY FRAME_SIZE_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task DeclaredFrameLength_Above16KiB_GoAwayFrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Claim 16385 bytes without sending the payload — Copy must reject before ReadExact.
        const int oversize = 16385;
        var hdr = new byte[9];
        hdr[0] = (byte)((oversize >> 16) & 0xff);
        hdr[1] = (byte)((oversize >> 8) & 0xff);
        hdr[2] = (byte)(oversize & 0xff);
        hdr[3] = (byte)Http2FrameType.Data;
        hdr[4] = 0;
        hdr[5] = 0;
        hdr[6] = 0;
        hdr[7] = 0;
        hdr[8] = 1; // stream 1
        await raw.Connection.GetStream().WriteAsync(hdr);

        var (type, code) = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, code);
    }

}
