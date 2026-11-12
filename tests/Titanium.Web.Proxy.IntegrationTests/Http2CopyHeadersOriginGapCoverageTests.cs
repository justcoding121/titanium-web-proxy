using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Remaining Copy.Headers + OriginConnection CreateAsync gaps:
///     :protocol on non-CONNECT, extended CONNECT validation, and SETTINGS_MAX_FRAME_SIZE upper bound.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyHeadersOriginGapCoverageTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<Http2RawFrame.Frame> ReadSignificantFrameAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        Http2RawFrame.Frame frame;
        do
        {
            frame = await raw.Connection.ReadFrameAsync().WaitAsync(timeout);
        } while (frame.Type is Http2FrameType.Settings or Http2FrameType.WindowUpdate);

        return frame;
    }

    private static Func<Http2RawFrame.Connection, Task> NoOpOriginHandler() => async conn =>
    {
        await conn.SendInitialSettingsAsync();
        try { while (true) await conn.ReadFrameAsync(); }
        catch { /* torn down */ }
    };

    private static (ProxyServer Proxy, TestSuite Suite) CreateH1ToH2BridgeProxy()
    {
        var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        return (proxy, suite);
    }

    private static byte[] SettingsEntry(Http2SettingsId id, uint value)
    {
        var payload = new byte[6];
        payload[0] = (byte)(((int)id >> 8) & 0xff);
        payload[1] = (byte)((int)id & 0xff);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(2), value);
        return payload;
    }

    /// <summary>
    ///     RFC 8441 §5: :protocol is only valid on CONNECT. Bug class: accepted on GET.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_ProtocolPseudoOnGet_TriggersRstProtocolError()
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
            catch { /* torn down */ }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true; // forces semantic enforcement

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/"),
             (":protocol", "websocket")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.RstStream, frame.Type,
            ":protocol on GET must RST the stream.");
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)));
        Assert.IsFalse(originContacted, ":protocol on GET must not reach the origin.");
    }

    /// <summary>
    ///     RFC 8441 §5: extended CONNECT requires :scheme, :path, and :authority.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_ExtendedConnect_MissingPath_TriggersRstProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // CONNECT + :protocol without :path.
        var block = raw.Connection.EncodeHeaders(
            [(":method", "CONNECT"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"),
             (":protocol", "websocket")],
            [("sec-websocket-version", "13"), ("sec-websocket-key", "dGhlIHNhbXBsZSBub25jZQ==")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.RstStream, frame.Type,
            "Extended CONNECT missing :path must RST the stream.");
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)));
    }

    /// <summary>
    ///     Bug class: extended CONNECT is accepted when EnableRfc8441 is false.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_ExtendedConnect_WhenRfc8441Disabled_TriggersRstRefusedStream()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = false;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "CONNECT"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/ws"),
             (":protocol", "websocket")],
            [("sec-websocket-version", "13"), ("sec-websocket-key", "dGhlIHNhbXBsZSBub25jZQ==")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.RstStream, frame.Type,
            "Extended CONNECT with RFC 8441 disabled must RST the stream.");
        Assert.AreEqual((int)Http2ErrorCode.RefusedStream,
            BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)));
    }

    /// <summary>
    ///     RFC 7540 §6.5.2: SETTINGS_MAX_FRAME_SIZE above 2^24-1 is a connection PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginMaxFrameSizeTooLarge_Returns502()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            // 16777216 = 2^24 — one above the legal maximum.
            await conn.WriteFrameAsync(Http2FrameType.Settings, 0, (Http2FrameFlag)0,
                SettingsEntry(Http2SettingsId.MaxFrameSize, 16_777_216u));
            try { while (true) await conn.ReadFrameAsync(); }
            catch { /* torn down */ }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var owned = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var response = await client.GetAsync(origin.Url);
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode,
            "SETTINGS_MAX_FRAME_SIZE above 16777215 must fail CreateAsync as 502.");
    }

    /// <summary>
    ///     Bug class: a header added in BeforeRequest is dropped because MITM still relays the
    ///     captured compressed request HEADERS block unchanged.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task BeforeRequest_AddedHeader_ReachesOriginViaReencode()
    {
        string? seenInjected = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, headers, _) = await conn.ReadRequestAsync();
            seenInjected = headers.FirstOrDefault(h => h.Name == "x-before-req").Value;
            var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "2")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("ok"));
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        proxy.ViaHeaderPseudonym = string.Empty;
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Headers.AddHeader("x-before-req", "injected");
            return Task.CompletedTask;
        };

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/hdr")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, respHeaders, _) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", respHeaders.Single(h => h.Name == ":status").Value);
        Assert.AreEqual("injected", seenInjected,
            "Header added in BeforeRequest must reach the origin via HPACK re-encode.");
    }
}
