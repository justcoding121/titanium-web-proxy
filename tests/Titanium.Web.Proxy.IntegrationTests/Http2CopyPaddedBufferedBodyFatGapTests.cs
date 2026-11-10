using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: padded DATA while BeforeRequest buffers the body
///     via GetRequestBody, and a legal padded DATA body that still round-trips.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyPaddedBufferedBodyFatGapTests
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
                foreach (var h in headers)
                {
                    if (h.Name == ":status")
                        return h.Value;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Bug class: PADDED DATA with pad covering the payload is accepted while buffering
    ///     GetRequestBody, corrupting the buffered body or hanging the handler.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task BufferedBody_PaddedDataExceedsPayload_GoAwayProtocolError()
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
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += async (_, e) =>
        {
            _ = await e.GetRequestBody();
        };

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/pad-buf"),
             ("content-length", "1")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);

        // Pad length 3 with only 3 payload bytes → pad covers everything.
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Data, 1, Http2FrameFlag.Padded | Http2FrameFlag.EndStream,
            new byte[] { 3, 0x41, 0x42 });

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: legal PADDED DATA while GetRequestBody buffers never reaches the handler.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task BufferedBody_LegalPaddedData_RoundTrips()
    {
        byte[]? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, body) = await conn.ReadRequestAsync();
                seen = body;
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += async (_, e) =>
        {
            _ = await e.GetRequestBody();
        };

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/pad-ok"),
             ("content-length", "2")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);

        // pad-length=1, one data byte 'A', one padding byte.
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Data, 1, Http2FrameFlag.Padded | Http2FrameFlag.EndStream,
            new byte[] { 1, (byte)'A', 0x00 });

        Assert.AreEqual("204", await ReadStatusAsync(raw, TimeSpan.FromSeconds(12)));
        CollectionAssert.AreEqual(new byte[] { (byte)'A' }, seen);
    }
}
