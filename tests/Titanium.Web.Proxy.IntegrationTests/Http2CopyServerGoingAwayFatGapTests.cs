using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.Headers</c> suite for origin GOAWAY → <c>ServerGoingAway</c>:
///     refuse later client streams with REFUSED_STREAM instead of forwarding into a
///     connection the origin already abandoned.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyServerGoingAwayFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

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

        Assert.Fail("Timed out waiting for RST_STREAM or GOAWAY.");
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
    ///     Bug class: after origin GOAWAY(last-stream-id=N), a later client stream is still
    ///     forwarded; the origin never answers and the client hangs.
    /// </summary>
    [TestMethod]
    [Timeout(45_000)]
    public async Task OriginGoAway_LaterClientStream_IsRefusedLocally()
    {
        var originSawSecond = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders([(":status", "204")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);

            // GOAWAY: last-stream-id = this stream. Later client streams must be refused locally.
            var goAway = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(goAway.AsSpan(0, 4), streamId);
            BinaryPrimitives.WriteInt32BigEndian(goAway.AsSpan(4, 4), (int)Http2ErrorCode.NoError);
            await conn.WriteFrameAsync(Http2FrameType.GoAway, 0, (Http2FrameFlag)0, goAway);

            try
            {
                while (true)
                {
                    _ = await conn.ReadRequestAsync();
                    Interlocked.Increment(ref originSawSecond);
                }
            }
            catch
            {
                // Connection closed after GOAWAY / client drop.
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        // Compressed-relay (interception off) is the path that records ServerGoingAway
        // from origin GOAWAY and refuses later client streams locally.
        proxy.EnableHttpInterception = false;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var first = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/first")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, first, endStream: true);
        Assert.AreEqual("204", await ReadStatusAsync(raw, TimeSpan.FromSeconds(12)));

        // Origin GOAWAY is not required on the client leg; give the relay a moment to
        // record ServerGoingAway before opening a later stream (no SslStream WaitAsync slices).
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        var second = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/second")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(3, second, endStream: true);

        var (type, code, streamId) = await ReadRstForStreamAsync(raw, 3, TimeSpan.FromSeconds(12));
        Assert.AreEqual(Http2FrameType.RstStream, type,
            "A stream above the origin GOAWAY last-stream-id must be RST locally.");
        Assert.AreEqual(3, streamId);
        Assert.AreEqual((int)Http2ErrorCode.RefusedStream, code);
        Assert.AreEqual(0, Volatile.Read(ref originSawSecond),
            "Refused streams must not reach the origin after GOAWAY.");
    }

    private static async Task<(Http2FrameType Type, int Error, int StreamId)> ReadRstForStreamAsync(
        Http2RawClient raw, int expectedStreamId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(remaining);
            if (frame.Type == Http2FrameType.RstStream && frame.StreamId == expectedStreamId)
            {
                return (frame.Type,
                    BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)),
                    frame.StreamId);
            }
        }

        Assert.Fail($"Timed out waiting for RST_STREAM on stream {expectedStreamId}.");
        return default;
    }

    /// <summary>
    ///     Bug class: HEADERS without :method are treated as request trailers before any
    ///     main headers and hang the stream instead of failing fast.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task HeadersWithoutMethod_AsFirstBlock_AreRejected()
    {
        var originContacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try
            {
                _ = await conn.ReadRequestAsync();
                originContacted = true;
            }
            catch
            {
                // Expected: malformed request never reaches origin.
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // No :method / :path / :scheme — looks like trailers before main request headers.
        var block = raw.Connection.EncodeHeaders([], [("x-early-trailer", "1")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        // Product logs PROTOCOL_ERROR and should RST; drain any control frames first.
        Http2RawFrame.Frame? terminal = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && terminal is null)
        {
            try
            {
                var frame = await raw.Connection.ReadFrameAsync()
                    .WaitAsync(TimeSpan.FromSeconds(2));
                if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                    terminal = frame;
            }
            catch (TimeoutException)
            {
                break;
            }
            catch (Exception)
            {
                // Connection reset / tear-down after the protocol error also counts as rejection.
                terminal = new Http2RawFrame.Frame(Http2FrameType.RstStream, 1, (Http2FrameFlag)0, new byte[4]);
                break;
            }
        }

        Assert.IsNotNull(terminal,
            "Trailer-before-request must RST/GOAWAY or reset the connection.");
        Assert.IsFalse(originContacted);
    }
}
