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
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Process-visible edges in <c>Http2OriginConnection</c> (invalid SETTINGS, compressed
///     header-block budget) and the H2 MITM BeforeResponse re-encode path in
///     <c>Http2Helper.Copy.Headers</c>.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2OriginConnectionEdgeCoverageTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

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

    // -------------------------------------------------------------------------
    // Http2OriginConnection — compressed header-block budget
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Bug class: an origin response whose compressed HEADERS block exceeds
    ///     <see cref="ProxyResourceLimits.MaxHttp2CompressedHeaderBlockBytes"/> is accepted
    ///     (unbounded memory) instead of failing the bridged exchange as 502.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginCompressedHeaderBlockOverBudget_Returns502()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            // Literal-heavy block that stays large after HPACK (unique name/value).
            var block = conn.EncodeHeaders(
                [(":status", "200")],
                [("x-origin-compressed-bomb", new string('Z', 4000)), ("content-length", "0")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: true);
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;
        proxy.ResourceLimits = ProxyResourceLimits.Default.WithMaxHttp2CompressedHeaderBlockBytes(512);

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode,
            "Origin compressed header block over budget must surface as 502 to the H1 client.");
    }

    // -------------------------------------------------------------------------
    // Http2OriginConnection — invalid SETTINGS_MAX_FRAME_SIZE
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Bug class: RFC 7540 §6.5.2 out-of-range SETTINGS_MAX_FRAME_SIZE from the origin is
    ///     accepted, leaving the bridge writing frames the origin cannot handle.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginInvalidMaxFrameSizeSetting_Returns502()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            // First frame must be SETTINGS; make it illegal (below 2^14).
            await conn.WriteFrameAsync(Http2FrameType.Settings, 0, (Http2FrameFlag)0,
                SettingsEntry(Http2SettingsId.MaxFrameSize, 100));
            try { while (true) await conn.ReadFrameAsync(); }
            catch { /* torn down */ }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode,
            "Invalid origin SETTINGS_MAX_FRAME_SIZE must fail the bridged request as 502.");
    }

    /// <summary>
    ///     Bug class: origin's first frame is not SETTINGS; CreateAsync hangs or resets H1 with no 502.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginFirstFrameNotSettings_Returns502()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.WriteFrameAsync(Http2FrameType.Ping, 0, (Http2FrameFlag)0, new byte[8]);
            try { while (true) await conn.ReadFrameAsync(); }
            catch { /* torn down */ }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;
        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
    }

    /// <summary>
    ///     Bug class: SETTINGS on a non-zero stream id is accepted (RFC 9113 §6.5).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginSettingsOnNonZeroStream_Returns502()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.WriteFrameAsync(Http2FrameType.Settings, 1, (Http2FrameFlag)0,
                SettingsEntry(Http2SettingsId.MaxConcurrentStreams, 100));
            try { while (true) await conn.ReadFrameAsync(); }
            catch { /* torn down */ }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;
        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
    }

    /// <summary>
    ///     Bug class: SETTINGS payload length not a multiple of 6 is ignored.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginSettingsPayloadNotMultipleOf6_Returns502()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.WriteFrameAsync(Http2FrameType.Settings, 0, (Http2FrameFlag)0, new byte[4]);
            try { while (true) await conn.ReadFrameAsync(); }
            catch { /* torn down */ }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;
        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
    }

    /// <summary>
    ///     Bug class: SETTINGS_INITIAL_WINDOW_SIZE above 2^31-1 is accepted, overflowing send windows.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginInvalidInitialWindowSizeSetting_Returns502()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            // 0xFFFFFFFF casts to int -1 and trips TryApplyInitialWindowSize.
            await conn.WriteFrameAsync(Http2FrameType.Settings, 0, (Http2FrameFlag)0,
                SettingsEntry(Http2SettingsId.InitialWindowSize, 0xFFFFFFFFu));
            try { while (true) await conn.ReadFrameAsync(); }
            catch { /* torn down */ }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;
        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Http2OriginConnection — ENABLE_CONNECT_PROTOCOL downgrade 1→0
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Bug class: RFC 8441 forbids downgrading SETTINGS_ENABLE_CONNECT_PROTOCOL from 1 to 0;
    ///     accepting it leaves the bridge believing extended CONNECT is still available.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginEnableConnectProtocolDowngrade_Returns502()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsWithConnectProtocolAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            // Illegal mid-connection downgrade after previously advertising 1.
            await conn.WriteFrameAsync(Http2FrameType.Settings, 0, (Http2FrameFlag)0,
                SettingsEntry(Http2SettingsId.EnableConnectProtocol, 0));

            // If the proxy wrongly accepts the downgrade it may still answer; either way the
            // connection should be torn down so the H1 client does not hang forever.
            try
            {
                var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "0")]);
                await conn.WriteHeaderBlockAsync(streamId, block, endStream: true);
            }
            catch
            {
                /* connection already closed by proxy */
            }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode,
            "ENABLE_CONNECT_PROTOCOL 1→0 downgrade must fail the bridged exchange as 502.");
    }

    // -------------------------------------------------------------------------
    // Copy.Headers — BeforeResponse header mutation reaches client
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Bug class: a header added in BeforeResponse is dropped because the MITM path
    ///     relays the captured compressed response HEADERS block unchanged.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task BeforeResponse_AddedHeader_ReachesClientViaReencodeOrAppend()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var block = conn.EncodeHeaders(
                [(":status", "200")],
                [("content-length", "2")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("ok"));
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        proxy.ViaHeaderPseudonym = string.Empty;

        proxy.BeforeResponse += (_, e) =>
        {
            e.HttpClient.Response.Headers.AddHeader("x-before-resp", "injected");
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        client.Timeout = TimeSpan.FromSeconds(20);
        using var response = await client.GetAsync(origin.Url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.TryGetValues("x-before-resp", out var values)
                      || response.Content.Headers.TryGetValues("x-before-resp", out values),
            "Header added in BeforeResponse must reach the H2 client.");
        Assert.AreEqual("injected", values!.First());
    }
}
