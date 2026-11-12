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
///     Fat <c>Http2Helper.Copy</c> / <c>Copy.Headers</c> suite: successful CONTINUATION
///     reassembly, CONTINUATION without open block, SETTINGS ACK mid-session, client GOAWAY
///     after a successful request, and PRIORITY-flagged HEADERS that still round-trip.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyContinuationSuccessFatGapTests
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

    [TestMethod]
    [Timeout(30_000)]
    public async Task Continuation_SplitRequestHeaders_RoundTrips()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, headers, _) = await conn.ReadRequestAsync();
            Assert.AreEqual("/split", headers.First(h => h.Name == ":path").Value);
            var resp = conn.EncodeHeaders([(":status", "204")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/split")],
            [("x-a", "1"), ("x-b", "2"), ("x-c", "3")]);
        // Force CONTINUATION by splitting below a typical single-frame size.
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true, maxFrameSize: 16);

        Assert.AreEqual("204", await ReadStatusAsync(raw, TimeSpan.FromSeconds(12)));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Continuation_WithoutOpenHeaderBlock_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Continuation, 1, Http2FrameFlag.EndHeaders, new byte[] { 0x82 });

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_Ack_IsAccepted_AndRequestStillSucceeds()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders([(":status", "200")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
            await conn.WriteFrameAsync(
                Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.UTF8.GetBytes("ack-ok"));
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Empty SETTINGS ACK (flag only) — proxy must not GOAWAY.
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Settings, 0, Http2FrameFlag.Ack, Array.Empty<byte>());

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/ack")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        Assert.AreEqual("200", await ReadStatusAsync(raw, TimeSpan.FromSeconds(12)));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_GoAway_AfterSuccessfulGet_DoesNotHang()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders([(":status", "204")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/g")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        Assert.AreEqual("204", await ReadStatusAsync(raw, TimeSpan.FromSeconds(12)));

        var goAway = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(goAway.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32BigEndian(goAway.AsSpan(4, 4), (int)Http2ErrorCode.NoError);
        await raw.Connection.WriteFrameAsync(Http2FrameType.GoAway, 0, (Http2FrameFlag)0, goAway);
        await Task.Delay(200);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Data_OnIdleStream_GoAwayOrRst()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Data, 1, Http2FrameFlag.EndStream, Encoding.UTF8.GetBytes("orphan"));

        var (type, _) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(type is Http2FrameType.RstStream or Http2FrameType.GoAway);
    }
}
