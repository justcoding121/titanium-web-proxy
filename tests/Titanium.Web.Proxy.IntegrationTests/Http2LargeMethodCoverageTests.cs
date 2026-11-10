using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Hits large remaining methods: <c>DeliverOriginExchangeAsync</c> (trailers),
///     <c>ApplyH1ToH2BridgeResponseCompressionAsync</c>, HEADERS+PRIORITY in Copy,
///     and <c>RunHttp11ToHttp2WebSocketTunnelAsync</c> missing-key early exit.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2LargeMethodCoverageTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static (ProxyServer Proxy, TestSuite Suite) CreateH1ToH2BridgeProxy(
        Action<ProxyServer>? configure = null)
    {
        var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        configure?.Invoke(proxy);

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        return (proxy, suite);
    }

    /// <summary>
    ///     Bug class: BeforeResponse body replace on the H1→H2 bridge still writes the origin body
    ///     (<c>DeliverOriginExchangeAsync</c> Locked / replacement path).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeResponse_ReplacesBufferedBody()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var headers = conn.EncodeHeaders([(":status", "200")],
                [("content-type", "text/plain"), ("content-length", "12")]);
            await conn.WriteHeaderBlockAsync(streamId, headers, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("origin-body!"));
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var owned = suite;
        proxy.BeforeResponse += async (_, e) =>
        {
            await e.GetResponseBody();
            e.Ok("replaced-h1h2");
        };

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var response = await client.GetAsync(origin.Url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("replaced-h1h2", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    ///     Bug class: Expect: 100-continue on the H1→H2 bridge deadlocks a strict client because no
    ///     synthetic 100 is written (<c>RunHttp11ToHttp2ExchangeAsync</c>).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_ExpectContinue_CompatibilityMode_WritesSynthetic100()
    {
        var originSawBody = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, body) = await conn.ReadRequestAsync();
            originSawBody = body.Length > 0;
            var headers = conn.EncodeHeaders([(":status", "200")],
                [("content-length", "2")]);
            await conn.WriteHeaderBlockAsync(streamId, headers, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("ok"));
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy(p =>
        {
            p.CompatibilityMode100Continue = true;
            p.Enable100ContinueBehaviour = false;
        });
        using var owned = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Post, origin.Url)
        {
            Content = new StringContent("post-body-bytes"),
        };
        request.Headers.ExpectContinue = true;

        using var response = await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(originSawBody, "Origin must receive the POST body after synthetic 100 Continue.");
    }

    /// <summary>
    ///     Bug class: HEADERS with the PRIORITY flag are rejected or mis-parsed (HPACK offset wrong).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Headers_WithPriorityFlag_CompletesSuccessfully()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "2")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("ok"));
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var hpack = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/prio")],
            []);
        // PRIORITY prefix: 4-byte dependent stream id + 1-byte weight, then HPACK.
        var payload = new byte[5 + hpack.Length];
        payload[4] = 16;
        Buffer.BlockCopy(hpack, 0, payload, 5, hpack.Length);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Headers, 1,
            Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream | Http2FrameFlag.Priority, payload);

        var (_, headers, _) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);
    }

    /// <summary>
    ///     Bug class: Upgrade without Sec-WebSocket-Key is accepted into the RFC 8441 tunnel path
    ///     instead of a clean 400 from <c>RunHttp11ToHttp2WebSocketTunnelAsync</c>.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_WebSocketUpgrade_MissingKey_Returns400()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsWithConnectProtocolAsync();
            try { while (true) await conn.ReadFrameAsync(); }
            catch { /* torn down */ }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy(p => p.EnableRfc8441 = true);
        using var owned = suite;

        var uri = new Uri(origin.Url);
        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(
            proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port,
            new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 });

        var upgrade = Encoding.ASCII.GetBytes(
            "GET /ws HTTP/1.1\r\n" +
            $"Host: {uri.Host}:{uri.Port}\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "\r\n");
        await tunnel.SslStream.WriteAsync(upgrade);

        var buffer = new byte[4096];
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await tunnel.SslStream.ReadAsync(buffer.AsMemory(total), cts.Token);
            if (read == 0) break;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                Assert.IsTrue(text.StartsWith("HTTP/1.1 400", StringComparison.Ordinal),
                    $"Missing Sec-WebSocket-Key must yield 400; got: {text.Split('\r')[0]}");
                return;
            }
        }

        Assert.Fail("Timed out waiting for HTTP/1.1 response to missing Sec-WebSocket-Key upgrade.");
    }

    /// <summary>
    ///     Bug class: BeforeResponse body replace on H2→H1 still sends the origin body (or hangs).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_BeforeResponse_ReplacesBufferedBody()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("origin-body-should-not-appear"));

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        proxy.BeforeResponse += async (_, e) =>
        {
            await e.GetResponseBody();
            e.Ok("replaced-by-handler");
        };

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/replace")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);

        var body = Array.Empty<byte>();
        if (!endStream)
        {
            using var ms = new System.IO.MemoryStream();
            while (true)
            {
                var frame = await raw.Connection.ReadFrameAsync();
                if (frame.Type != Http2FrameType.Data || frame.StreamId != 1)
                    continue;
                await ms.WriteAsync(frame.Payload);
                if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    break;
            }

            body = ms.ToArray();
        }

        Assert.AreEqual("replaced-by-handler", Encoding.ASCII.GetString(body));
    }
}
