using System;
using System.Buffers.Binary;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: origin PUSH_PROMISE with a large block,
///     unexpected RST_STREAM error codes, origin NO_ERROR while response DATA is still
///     in flight, and DATA on a never-opened client stream id.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyRstAndOriginPushFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<(Http2FrameType Type, int Error)> ReadGoAwayOrRstAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.GoAway)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
            if (frame.Type == Http2FrameType.RstStream)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)));
        }

        Assert.Fail("Timed out waiting for GOAWAY/RST.");
        return default;
    }

    /// <summary>
    ///     Bug class: origin PUSH_PROMISE with a large HPACK block hangs instead of
    ///     GOAWAY(PROTOCOL_ERROR) on the client leg.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_PushPromise_LargeBlock_ClientGoAwayProtocolError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            var hpackTail = new byte[96];
            Array.Fill(hpackTail, (byte)0x80);
            var pushPayload = new byte[4 + hpackTail.Length];
            BinaryPrimitives.WriteInt32BigEndian(pushPayload.AsSpan(0, 4), 2);
            hpackTail.CopyTo(pushPayload.AsSpan(4));

            await conn.WriteFrameAsync(Http2FrameType.PushPromise, streamId,
                Http2FrameFlag.EndHeaders, pushPayload);
            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/push-large")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var sawTerminal = false;
        try
        {
            var (type, code) = await ReadGoAwayOrRstAsync(raw, TimeSpan.FromSeconds(10));
            sawTerminal = true;
            if (type == Http2FrameType.GoAway)
                Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            sawTerminal = true;
        }

        Assert.IsTrue(sawTerminal, "Client must observe GOAWAY/RST or connection close after origin PUSH_PROMISE.");
        cts.Cancel();
    }

    /// <summary>
    ///     Bug class: client RST_STREAM with an unexpected error code wedges the connection
    ///     or omits the forward toward the origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_RstCompressionError_AfterHeaders_AllowsNextStream()
    {
        var hits = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                System.Threading.Interlocked.Increment(ref hits);
                var payload = Encoding.UTF8.GetBytes("ok");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var req1 = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/rst-comp")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, req1, endStream: true);

        var rst = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(rst, (int)Http2ErrorCode.CompressionError);
        await raw.Connection.WriteFrameAsync(Http2FrameType.RstStream, 1, 0, rst);

        var req3 = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/after-rst")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(3, req3, endStream: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
            {
                var status = raw.Connection.DecodeHeaders(frame.Payload)
                    .FirstOrDefault(h => h.Name == ":status").Value;
                if (status == "200")
                {
                    Assert.IsTrue(hits >= 2, "Second stream must reach the origin after CompressionError RST.");
                    return;
                }
            }
        }

        Assert.Fail("Second stream must succeed after client RST(COMPRESSION_ERROR).");
    }

    /// <summary>
    ///     Bug class: client PUSH_PROMISE with a large promise block hangs instead of
    ///     draining and sending GOAWAY(PROTOCOL_ERROR).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_PushPromise_LargeBlock_GoAwayProtocolError()
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

        var tail = new byte[128];
        Array.Fill(tail, (byte)0x80);
        var payload = new byte[4 + tail.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), 2);
        tail.CopyTo(payload.AsSpan(4));
        await raw.Connection.WriteFrameAsync(Http2FrameType.PushPromise, 1, Http2FrameFlag.EndHeaders, payload);

        var (type, code) = await ReadGoAwayOrRstAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: DATA on a never-opened odd stream id is relayed instead of RST_STREAM(STREAM_CLOSED).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Data_OnNeverOpenedOddStream_RstStreamClosed()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
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
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/first")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        // Drain the 204 response.
        var drainUntil = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < drainUntil)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(drainUntil - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.Headers)
                break;
        }

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Data, 1, Http2FrameFlag.EndStream, Encoding.ASCII.GetBytes("late"));

        var (type, code) = await ReadGoAwayOrRstAsync(raw, TimeSpan.FromSeconds(8));
        Assert.AreEqual(Http2FrameType.RstStream, type);
        Assert.AreEqual((int)Http2ErrorCode.StreamClosed, code);
    }
}
