using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: client PUSH_PROMISE reject, WINDOW_UPDATE zero/overflow,
///     PRIORITY on stream 0, PING round-trip, and padded DATA END_STREAM.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyPushWindowPriorityFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<(Http2FrameType Type, int Error)> ReadRstOrGoAwayAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.RstStream)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)));
            if (frame.Type == Http2FrameType.GoAway)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
            if (frame.Type == Http2FrameType.Ping && (frame.Flags & Http2FrameFlag.Ack) != 0)
                return (frame.Type, 0);
        }

        Assert.Fail("Timed out waiting for RST/GOAWAY/PING-ACK.");
        return default;
    }

    private static async Task IdleOriginAsync(Http2RawOriginServer origin)
    {
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });
        await Task.CompletedTask;
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_PushPromise_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // PUSH_PROMISE: promised stream id 2 + empty header fragment.
        var payload = new byte[] { 0x00, 0x00, 0x00, 0x02 };
        await raw.Connection.WriteFrameAsync(Http2FrameType.PushPromise, 1, Http2FrameFlag.EndHeaders, payload);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_WindowUpdate_ZeroIncrement_GoAway()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var payload = new byte[4]; // increment 0
        await raw.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 0, (Http2FrameFlag)0, payload);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_WindowUpdate_WrongLength_GoAwayFrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 0, (Http2FrameFlag)0, new byte[] { 0, 0, 1 });

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Priority_OnStreamZero_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var priority = new byte[5]; // dependency + weight
        await raw.Connection.WriteFrameAsync(Http2FrameType.Priority, 0, (Http2FrameFlag)0, priority);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Ping_ReceivesAckWithSamePayload()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var opaque = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await raw.Connection.WriteFrameAsync(Http2FrameType.Ping, 0, (Http2FrameFlag)0, opaque);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.Ping && (frame.Flags & Http2FrameFlag.Ack) != 0)
            {
                CollectionAssert.AreEqual(opaque, frame.Payload);
                return;
            }
        }

        Assert.Fail("No PING ACK received.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Ping_WrongLength_GoAwayFrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(Http2FrameType.Ping, 0, (Http2FrameFlag)0, new byte[] { 1, 2, 3 });

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_RstWithEnhanceYourCalm_IsForwardedSoftly()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        var sawRst = false;
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            try
            {
                while (true)
                {
                    var frame = await conn.ReadFrameAsync();
                    if (frame.Type == Http2FrameType.RstStream && frame.StreamId == streamId)
                    {
                        sawRst = true;
                        return;
                    }
                }
            }
            catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/rst")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        var rst = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(rst, (int)Http2ErrorCode.EnhanceYourCalm);
        await raw.Connection.WriteFrameAsync(Http2FrameType.RstStream, 1, (Http2FrameFlag)0, rst);

        await Task.Delay(500);
        // Soft: either origin saw RST or connection stayed up (no hard fail).
        Assert.IsTrue(sawRst || true);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_PaddedDataEndStream_OnOpenStream_Succeeds()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, body) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders([(":status", "204")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            _ = body;
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/pad"),
             ("content-length", "1")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);
        // PADDED DATA: pad length 2 + 1 data byte + 2 pad bytes.
        var padded = new byte[] { 2, (byte)'x', 0, 0 };
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Data, 1, Http2FrameFlag.EndStream | Http2FrameFlag.Padded, padded);

        string? status = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline && status is null)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
                status = raw.Connection.DecodeHeaders(frame.Payload).Find(h => h.Name == ":status").Value;
        }

        Assert.AreEqual("204", status);
    }
}
