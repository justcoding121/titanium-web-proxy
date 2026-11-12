using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Coverage for two important paths that were previously not exercised:
///     <list type="bullet">
///         <item>
///             <term>GOAWAY retry</term>
///             <description>
///                 <c>Http11ToHttp2BridgeHandler.cs</c> lines 693–714: when the H2 origin sends
///                 GOAWAY with a last-stream-id below the inflight stream, RFC 7540 §6.8 guarantees
///                 the stream was never processed and the proxy must replay the request on a fresh
///                 connection instead of returning a 502.
///             </description>
///         </item>
///         <item>
///             <term>H2 body-budget enforcement</term>
///             <description>
///                 <c>Http2Helper.Copy.cs</c> lines 1649–1674: when <c>BeforeRequest</c> calls
///                 <c>GetRequestBody()</c> on an H2 stream and the cumulative DATA frames exceed
///                 <see cref="ProxyServer.MaxBufferedBodyBytes"/>, the proxy must RST_STREAM the
///                 client stream and never forward the oversized body to the origin.
///             </description>
///         </item>
///     </list>
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http11ToHttp2BridgeGoAwayRetryTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Creates an H1-client → proxy → H2-origin bridge, with the proxy forced to speak HTTP/2
    ///     toward any origin.  Mirrors the helper in <c>Http2CopyOriginBridgeCoverageTests</c>.
    /// </summary>
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

    // =========================================================================
    // Http11ToHttp2BridgeHandler — GOAWAY-triggered retry (lines 693-714)
    // =========================================================================

    /// <summary>
    ///     Bug class: when the H2 origin sends GOAWAY with last-stream-id 0 the proxy must
    ///     recognise that stream 1 was never processed and replay the exact same GET on a new
    ///     connection.  Without the retry the client would receive a synthetic 502.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginGoAway_SafeToRetry_RequestIsReplayed()
    {
        var headersSeenCount = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();

            // ReadRequestAsync skips SETTINGS/WINDOW_UPDATE and returns on the HEADERS+END_STREAM.
            var (streamId, _, _) = await conn.ReadRequestAsync();

            var n = Interlocked.Increment(ref headersSeenCount);
            if (n == 1)
            {
                // First real request: GOAWAY claiming no streams were processed (lastId=0 < streamId=1).
                // RFC 7540 §6.8: the proxy is safe to retry streams above last-stream-id.
                var goaway = new byte[8];
                BinaryPrimitives.WriteInt32BigEndian(goaway, 0);             // lastStreamId = 0
                BinaryPrimitives.WriteInt32BigEndian(goaway.AsSpan(4), 0);   // errorCode = NO_ERROR
                await conn.WriteFrameAsync(Http2FrameType.GoAway, 0, (Http2FrameFlag)0, goaway);
            }
            else
            {
                // Retried request: respond normally.
                var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "5")]);
                await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                    "retry"u8.ToArray());
            }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var _ = suite;
        // Register a BeforeRequest handler so interception is active (required for relayInterim path).
        proxy.BeforeRequest += (_, __) => Task.CompletedTask;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using var response = await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "H1 client must see 200 after the GOAWAY-triggered H2-origin retry.");
        Assert.AreEqual("retry", await response.Content.ReadAsStringAsync(),
            "Response body must be from the retried (second) connection, not a synthetic error.");
        Assert.IsTrue(headersSeenCount >= 2,
            $"Expected at least 2 H2 request HEADERS (first got GOAWAY, second succeeded); got {headersSeenCount}.");
    }

    // =========================================================================
    // Http2Helper.Copy.cs — whole-body buffering budget (lines 1649-1674)
    // =========================================================================

    /// <summary>
    ///     Bug class: the per-frame size cap (SETTINGS_MAX_FRAME_SIZE) is not a cumulative cap.
    ///     When <c>BeforeRequest</c> calls <c>GetRequestBody()</c> on an H2 stream, the proxy
    ///     must track the cumulative DATA size and RST_STREAM the client stream if it exceeds
    ///     <see cref="ProxyServer.MaxBufferedBodyBytes"/>.  Without this, an H2 client could
    ///     exhaust proxy memory by posting many small DATA frames that each stay within the
    ///     per-frame limit but together exceed the configured cap.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_RequestBody_BudgetExceeded_ReturnsStreamError()
    {
        var originSawRequest = false;
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async ctx =>
        {
            originSawRequest = true;
            await ctx.Response.WriteAsync("should-not-arrive");
        });

        var proxy = testSuite.GetProxy();
        proxy.MaxBufferedBodyBytes = 512;     // well below the 4 096-byte body below
        proxy.EnableHttp2 = true;
        proxy.BeforeRequest += async (_, e) =>
        {
            if (e.HttpClient.Request.HasBody)
                await e.GetRequestBody();
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        client.Timeout = TimeSpan.FromSeconds(20);

        // POST body that exceeds the 512-byte cumulative DATA budget.
        var oversizedBody = new StringContent(new string('x', 4096));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(server.ListeningHttpsUrl))
                {
                    Content = oversizedBody,
                    Version = new Version(2, 0),
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                await client.SendAsync(req);
            },
            "An H2 POST body exceeding MaxBufferedBodyBytes must produce a stream error (RST_STREAM), " +
            "not a successful 200.");

        Assert.IsFalse(originSawRequest,
            "The oversized H2 request body must not reach the origin server.");
    }

    /// <summary>
    ///     Baseline: an H2 POST body within the budget must be forwarded normally.
    ///     Guards against over-eager enforcement that rejects valid requests.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_RequestBody_WithinBudget_IsForwarded()
    {
        string? originBody = null;
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var reader = new System.IO.StreamReader(ctx.Request.Body);
            originBody = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.MaxBufferedBodyBytes = 8192;    // plenty for 64 bytes
        proxy.EnableHttp2 = true;
        proxy.BeforeRequest += async (_, e) =>
        {
            if (e.HttpClient.Request.HasBody)
                await e.GetRequestBody();
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        client.Timeout = TimeSpan.FromSeconds(20);

        var payload = new string('y', 64);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(server.ListeningHttpsUrl))
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/plain"),
            Version = new Version(2, 0),
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using var response = await client.SendAsync(req).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "An H2 POST body within MaxBufferedBodyBytes must be forwarded normally.");
        Assert.IsNotNull(originBody);
        Assert.AreEqual(payload, originBody,
            "The 64-byte body must reach the origin intact.");
    }
}
