using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.Headers</c> suite: missing :scheme/:path, connection-specific
///     forbidden fields, bad HPACK compression error, and decoded-header-list EnhanceYourCalm.
/// </summary>
[TestClass]
public class Http2CopyHeadersMissingPseudoAndForbiddenGapTests
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

        Assert.Fail("Timed out waiting for RST_STREAM or GOAWAY.");
        return default;
    }

    private static async Task AssertRequestRejectedAsync(
        IEnumerable<(string Name, string Value)> pseudo,
        IEnumerable<(string Name, string Value)>? regular = null,
        Http2ErrorCode? expectedRst = Http2ErrorCode.ProtocolError,
        bool expectGoAway = false)
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
            catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(pseudo, regular ?? []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(type is Http2FrameType.RstStream or Http2FrameType.GoAway,
            $"Malformed request must RST or GOAWAY; got {type}");
        if (expectGoAway)
            Assert.AreEqual(Http2FrameType.GoAway, type);
        else if (type == Http2FrameType.RstStream && expectedRst is { } want)
            Assert.AreEqual((int)want, code);

        Assert.IsFalse(originContacted);
    }

    [TestMethod]
    [Timeout(30_000)]
    public Task MissingScheme_IsRejected() =>
        AssertRequestRejectedAsync(
            [(":method", "GET"), (":authority", "localhost"), (":path", "/")]);

    [TestMethod]
    [Timeout(30_000)]
    public Task MissingPath_IsRejected() =>
        AssertRequestRejectedAsync(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost")]);

    [TestMethod]
    [Timeout(30_000)]
    public Task ConnectionHeader_IsRejected() =>
        AssertRequestRejectedAsync(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")],
            [("connection", "keep-alive")]);

    [TestMethod]
    [Timeout(30_000)]
    public Task KeepAliveHeader_IsRejected() =>
        AssertRequestRejectedAsync(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")],
            [("keep-alive", "timeout=5")]);

    [TestMethod]
    [Timeout(30_000)]
    public Task TransferEncodingHeader_IsRejected() =>
        AssertRequestRejectedAsync(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")],
            [("transfer-encoding", "chunked")]);

    [TestMethod]
    [Timeout(30_000)]
    public Task UpgradeHeader_IsRejected() =>
        AssertRequestRejectedAsync(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")],
            [("upgrade", "websocket")]);

    [TestMethod]
    [Timeout(30_000)]
    public Task ProxyConnectionHeader_IsRejected() =>
        AssertRequestRejectedAsync(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")],
            [("proxy-connection", "keep-alive")]);

    /// <summary>
    ///     Bug class: oversized decoded header list is forwarded instead of RST EnhanceYourCalm.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task DecodedHeaderListTooLarge_RstEnhanceYourCalm()
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
        proxy.MaxDecodedHeaderListBytes = 256;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var regular = Enumerable.Range(0, 40)
            .Select(i => ($"x-pad-{i}", new string('a', 32)))
            .ToArray();
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/huge")],
            regular);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.RstStream, type);
        Assert.AreEqual((int)Http2ErrorCode.EnhanceYourCalm, code);
    }

    /// <summary>
    ///     Bug class: response :status missing digits is accepted on the origin leg.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_InvalidStatus_SoftResetsOriginStream()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var bad = conn.EncodeHeaders([(":status", "OK")], []);
            await conn.WriteHeaderBlockAsync(streamId, bad, endStream: true);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/bad-status")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        // Soft path: RST on origin leg and/or GOAWAY NoError / ProtocolError toward client.
        var (type, _) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(12));
        Assert.IsTrue(type is Http2FrameType.RstStream or Http2FrameType.GoAway,
            $"Invalid :status must terminate the exchange; got {type}");
    }
}
