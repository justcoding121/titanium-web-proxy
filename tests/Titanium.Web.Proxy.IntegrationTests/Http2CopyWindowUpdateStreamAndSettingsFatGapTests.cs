using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: stream WINDOW_UPDATE zero/overflow and
///     SETTINGS frames whose length is not a multiple of 6.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyWindowUpdateStreamAndSettingsFatGapTests
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

    private static async Task<(Http2FrameType Type, int Error, int StreamId)> ReadRstOrGoAwayAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.RstStream)
            {
                return (frame.Type,
                    BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)),
                    frame.StreamId);
            }

            if (frame.Type == Http2FrameType.GoAway)
            {
                return (frame.Type,
                    BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)),
                    0);
            }
        }

        Assert.Fail("Timed out waiting for RST/GOAWAY.");
        return default;
    }

    /// <summary>
    ///     Bug class: WINDOW_UPDATE(increment=0) on an open stream is ignored instead of RST.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Stream_WindowUpdate_ZeroIncrement_RstProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/wu0")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);

        var zero = new byte[4];
        await raw.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 1, 0, zero);

        var (type, code, streamId) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.RstStream, type);
        Assert.AreEqual(1, streamId);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: WINDOW_UPDATE that overflows the connection window is accepted.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Connection_WindowUpdate_Overflow_GoAwayFlowControlError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Default window is 65535; one 2^31-1 increment overflows past 2^31-1.
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, 0x7fffffff);
        await raw.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 0, 0, payload);

        var (type, code, _) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.FlowControlError, code);
    }

    /// <summary>
    ///     Bug class: SETTINGS whose length is not a multiple of 6 is applied.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_OddLength_GoAwayFrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // 5 bytes is not divisible by 6.
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Settings, 0, 0, new byte[] { 0, 1, 0, 0, 1 });

        var (type, code, _) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, code);
    }

    /// <summary>
    ///     Bug class: WINDOW_UPDATE with wrong length on a stream is ignored.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Stream_WindowUpdate_WrongLength_GoAwayFrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.WindowUpdate, 1, 0, new byte[] { 0, 0, 1 });

        var (type, code, _) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, code);
    }
}
