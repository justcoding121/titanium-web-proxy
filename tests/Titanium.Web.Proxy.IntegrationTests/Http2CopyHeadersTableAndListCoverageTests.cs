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
///     Behavior coverage for remaining <c>Http2Helper.Copy.Headers.cs</c> gaps:
///     decoded header-list refuse and BeforeRequest mutations that force a full
///     HPACK re-encode. Also covers <c>Http2OriginConnection</c> oversized origin
///     response headers (and the H1↔H2 bridge 502 mapping for that failure).
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyHeadersTableAndListCoverageTests
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

    // -------------------------------------------------------------------------
    // Header-list size refuse (Copy.Headers ProcessCompleteHeaderBlockAsync)
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Bug class: an oversize decoded header list is forwarded (or tears the whole
    ///     connection) instead of RST_STREAM(ENHANCE_YOUR_CALM) with HPACK state kept
    ///     intact so a subsequent stream can succeed.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_OversizedHeaderList_RstEnhanceYourCalm_ThenNextStreamSucceeds()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                try
                {
                    var (streamId, _, _) = await conn.ReadRequestAsync();
                    var block = conn.EncodeHeaders([(":status", "200")],
                        [("content-length", "2")]);
                    await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
                    await conn.WriteFrameAsync(Http2FrameType.Data, streamId,
                        Http2FrameFlag.EndStream, Encoding.ASCII.GetBytes("ok"));
                }
                catch
                {
                    return;
                }
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        // Small enough that a single large custom header overflows; large enough for a
        // normal GET's pseudo-headers alone.
        proxy.MaxDecodedHeaderListBytes = 400;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var oversized = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/too-big")],
            [("x-big", new string('a', 800))]);
        await raw.Connection.WriteHeaderBlockAsync(1, oversized, endStream: true);

        var rst = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.RstStream, rst.Type,
            "Oversized header list must RST the stream, not GOAWAY the connection.");
        Assert.AreEqual(1, rst.StreamId);
        Assert.AreEqual((int)Http2ErrorCode.EnhanceYourCalm,
            BinaryPrimitives.ReadInt32BigEndian(rst.Payload),
            "RST_STREAM must carry ENHANCE_YOUR_CALM (policy reject, HPACK still valid).");

        var ok = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/next")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(3, ok, endStream: true);

        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);
        if (!endStream)
        {
            var data = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(Http2FrameType.Data, data.Type);
        }
    }

    // -------------------------------------------------------------------------
    // BeforeRequest mutation → full HPACK re-encode
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Bug class: a BeforeRequest path rewrite is lost because the MITM path still
    ///     relays the captured compressed HEADERS block (or fails to re-encode).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task BeforeRequest_PathRewrite_ReachesOriginViaFullReencode()
    {
        string? seenPath = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, headers, _) = await conn.ReadRequestAsync();
            seenPath = headers.FirstOrDefault(h => h.Name == ":path").Value;
            var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "2")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("ok"));
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        proxy.ViaHeaderPseudonym = string.Empty; // exercise re-encode without Via append shortcut

        proxy.BeforeRequest += (_, e) =>
        {
            // Path change fails CapturedPath equality → skips compressed relay → full re-encode.
            e.HttpClient.Request.RequestUriString = "/rewritten-by-before";
            return Task.CompletedTask;
        };

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/original")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, headers, _) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);
        Assert.AreEqual("/rewritten-by-before", seenPath,
            "BeforeRequest path rewrite must reach the origin via full HPACK re-encode.");
    }

    // -------------------------------------------------------------------------
    // Http2OriginConnection — oversized origin response header list
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Bug class: <c>Http2OriginConnection.ProcessHeaderBlock</c> oversize origin headers
    ///     used to reset the H1 client instead of returning 502, because
    ///     <c>RunHttp11ToHttp2ExchangeAsync</c> filtered out <c>ProxyHttpException</c>.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginOversizedResponseHeaders_Returns502()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var block = conn.EncodeHeaders(
                [(":status", "200")],
                [("x-origin-big", new string('o', 2000)), ("content-length", "0")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: true);
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;
        proxy.MaxDecodedHeaderListBytes = 400;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode,
            "Oversized origin header list must fail the bridged stream as 502.");
    }
}
