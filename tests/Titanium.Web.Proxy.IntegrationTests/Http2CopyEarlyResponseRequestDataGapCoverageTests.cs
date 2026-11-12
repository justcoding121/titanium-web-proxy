using System;
using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Remaining <c>Http2Helper.Copy</c> arms: GOAWAY after stream-id-0 DATA must drain the
///     unread payload, and request DATA after BeforeRequest Ok must be absorbed quietly.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyEarlyResponseRequestDataGapCoverageTests
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
            Http2RawFrame.Frame frame;
            try
            {
                frame = await raw.Connection.ReadFrameAsync()
                    .WaitAsync(deadline - DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not AssertFailedException)
            {
                Assert.Fail($"Read while waiting for GOAWAY: {ex.Message}");
                return default;
            }

            if (frame.Type == Http2FrameType.RstStream)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)));
            if (frame.Type == Http2FrameType.GoAway)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
        }

        Assert.Fail("Timed out waiting for RST/GOAWAY.");
        return default;
    }

    /// <summary>
    ///     Bug class: DATA on stream 0 sends GOAWAY then the frame loop desyncs on the
    ///     unread payload so the client never observes GOAWAY.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Data_OnStreamZero_DrainsPayloadThenGoAway()
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

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Data, 0, Http2FrameFlag.EndStream, Encoding.ASCII.GetBytes("leftover"));

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: trailing client DATA after BeforeRequest Ok corrupts stream state
    ///     or contacts the origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task BeforeRequestOk_TrailingPostData_IsAbsorbedAndNextGetWorks()
    {
        var originHits = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, body) = await conn.ReadRequestAsync();
                var path = headers.Find(h => h.Name == ":path").Value;
                if (path.Contains("synthetic", StringComparison.Ordinal))
                {
                    Assert.Fail("Origin must not receive the synthetic POST.");
                }

                System.Threading.Interlocked.Increment(ref originHits);
                var payload = Encoding.UTF8.GetBytes("second-ok");
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
        proxy.BeforeRequest += (_, e) =>
        {
            if (e.HttpClient.Request.Url.Contains("synthetic", StringComparison.Ordinal))
            {
                e.Ok("short-circuit");
                return Task.CompletedTask;
            }

            return Task.CompletedTask;
        };

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/synthetic"),
             ("content-length", "16")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Data, 1, Http2FrameFlag.EndStream, Encoding.UTF8.GetBytes("upload-after-ok"));

        var status = await ReadStatusOnStreamAsync(raw, 1, TimeSpan.FromSeconds(12));
        Assert.AreEqual("200", status);

        var next = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/follow-up")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(3, next, endStream: true);
        status = await ReadStatusOnStreamAsync(raw, 3, TimeSpan.FromSeconds(12));
        Assert.AreEqual("200", status);
        Assert.AreEqual(1, originHits, "Only the follow-up GET may reach the origin.");
    }

    private static async Task<string?> ReadStatusOnStreamAsync(
        Http2RawClient raw, int expectStream, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.StreamId != expectStream)
                continue;
            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
            {
                var headers = raw.Connection.DecodeHeaders(frame.Payload);
                var status = headers.Find(h => h.Name == ":status").Value;
                if (status is not null)
                    return status;
            }
        }

        return null;
    }
}
