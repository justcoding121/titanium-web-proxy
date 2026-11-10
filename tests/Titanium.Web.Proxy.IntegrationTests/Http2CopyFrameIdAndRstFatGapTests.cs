using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: stream-id 0 on stream frames, non-zero on
///     connection frames, RST_STREAM length, and DATA on a just-closed stream.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyFrameIdAndRstFatGapTests
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
        }

        Assert.Fail("Timed out waiting for RST/GOAWAY.");
        return default;
    }

    private static async Task<string?> ReadStatusAsync(Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
            {
                var headers = raw.Connection.DecodeHeaders(frame.Payload);
                var status = headers.FirstOrDefault(h => h.Name == ":status").Value;
                if (status is not null)
                    return status;
            }
        }

        return null;
    }

    /// <summary>
    ///     Bug class: DATA on stream 0 is relayed instead of connection PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Data_OnStreamZero_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Data, 0, Http2FrameFlag.EndStream, Encoding.ASCII.GetBytes("x"));

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: HEADERS on stream 0 is accepted.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Headers_OnStreamZero_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "x"), (":path", "/")], []);
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Headers, 0, Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream, block);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: PING with non-zero stream id is ACKed instead of GOAWAY.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Ping_OnNonZeroStream_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(Http2FrameType.Ping, 1, 0, new byte[8]);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: SETTINGS on a stream id is applied instead of GOAWAY.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_OnNonZeroStream_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 3, 0, Array.Empty<byte>());

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: RST_STREAM with length != 4 is ignored.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task RstStream_WrongLength_GoAwayFrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(Http2FrameType.RstStream, 1, 0, new byte[] { 0, 0, 0 });

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, code);
    }

    /// <summary>
    ///     Bug class: DATA on a closed stream is forwarded to the origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Data_OnClosedStream_RstStreamClosed()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders([(":status", "204")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/done")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        Assert.AreEqual("204", await ReadStatusAsync(raw, TimeSpan.FromSeconds(12)));

        // Stream 1 is closed; another DATA must RST STREAM_CLOSED (not hang / GOAWAY).
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Data, 1, Http2FrameFlag.EndStream, Encoding.ASCII.GetBytes("late"));

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.RstStream, type);
        Assert.AreEqual((int)Http2ErrorCode.StreamClosed, code);
    }
}
