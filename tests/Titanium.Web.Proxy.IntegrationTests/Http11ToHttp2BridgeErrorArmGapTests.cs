using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Remaining <c>Http11ToHttp2BridgeHandler</c> error/short-circuit arms: MaxHeaderCount 431,
///     reverse-proxy NoDestination, WebTransport-not-advertised 501, and CancelRequest Ok.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http11ToHttp2BridgeErrorArmGapTests
{
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
    ///     Bug class: MaxHeaderCount breach on the H1→H2 bridge hangs / resets instead of 431.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_MaxHeaderCount_Returns431()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { _ = await conn.ReadRequestAsync(); } catch { }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy(p =>
        {
            p.ResourceLimits = ProxyResourceLimits.Create(
                maxHeaderLineBytes: 8 * 1024,
                maxHeaderCount: 8,
                maxHeaderAggregateBytes: 256 * 1024,
                maxEncodedBodyBytes: null,
                maxDecodedBodyBytes: null,
                maxDecompressionRatio: 200,
                maxConcurrentClients: null,
                maxConcurrentStreamsPerConnection: 100,
                maxPeerInitiatedIncompleteStreamResets: 100,
                maxOpenHeaderBlockFrames: 128,
                maxOpenHeaderBlockDuration: TimeSpan.FromSeconds(10),
                connectionPoolingEnabled: true,
                maxCachedConnectionsPerHost: 4,
                maxCertificateCacheEntries: null);
        });
        using var owned = suite;

        using var client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}"),
            UseProxy = true,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        })
        { Timeout = TimeSpan.FromSeconds(20) };

        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url);
        for (var i = 0; i < 24; i++)
            request.Headers.TryAddWithoutValidation($"x-h{i}", "v");

        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.RequestHeaderFieldsTooLarge, response.StatusCode);
    }

    /// <summary>
    ///     Bug class: reverse-proxy NoDestination on the H1→H2 bridge does not write a response.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_ReverseProxy_NoDestination_Returns5xx()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { _ = await conn.ReadRequestAsync(); } catch { }
        });

        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig { Id = "empty", Destinations = [] },
        ]);

        var (proxy, suite) = CreateH1ToH2BridgeProxy(p =>
        {
            p.ReverseProxy = new ReverseProxyOptions
            {
                Routes =
                [
                    new RouteConfig
                    {
                        Id = "r",
                        ClusterId = "empty",
                        Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    },
                ],
                Clusters = [new ClusterConfig { Id = "empty", Destinations = [] }],
                ClusterManager = manager,
                RouteMatcher = new RouteMatcher(),
                LoadBalancer = new LoadBalancer(),
                TransformEngine = new TransformEngine(),
            };
        });
        using var owned = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var response = await client.GetAsync(origin.Url);
        Assert.IsTrue((int)response.StatusCode >= 500,
            $"NoDestination must yield 5xx; got {(int)response.StatusCode}");
    }

    /// <summary>
    ///     Bug class: WebTransport upgrade on a listener that does not advertise WT returns 200/hangs
    ///     instead of 501.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_WebTransportUpgrade_WhenNotAdvertised_Returns501()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var owned = suite;

        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        await using var stream = tcp.GetStream();
        var uri = new Uri(origin.Url);
        var connect = Encoding.ASCII.GetBytes(
            $"CONNECT {uri.Host}:{uri.Port} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\n\r\n");
        await stream.WriteAsync(connect);
        var buf = new byte[1024];
        _ = await stream.ReadAsync(buf);

        await using var ssl = new System.Net.Security.SslStream(stream, false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(uri.Host);
        var upgrade = Encoding.ASCII.GetBytes(
            "CONNECT / HTTP/1.1\r\n" +
            $"Host: {uri.Host}:{uri.Port}\r\n" +
            ":protocol: webtransport\r\n" +
            "\r\n");
        // Extended CONNECT over H1 bridge uses Upgrade: websocket style; use the H1 WT request shape.
        upgrade = Encoding.ASCII.GetBytes(
            "GET /wt HTTP/1.1\r\n" +
            $"Host: {uri.Host}:{uri.Port}\r\n" +
            "Connection: Upgrade\r\n" +
            "Upgrade: webtransport\r\n\r\n");
        await ssl.WriteAsync(upgrade);

        var responseBytes = new byte[512];
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
        var n = await ssl.ReadAsync(responseBytes, cts.Token);
        var text = Encoding.ASCII.GetString(responseBytes, 0, n);
        Assert.IsTrue(
            text.Contains("501", StringComparison.Ordinal) ||
            text.Contains("Not Implemented", StringComparison.OrdinalIgnoreCase),
            $"WebTransport when not advertised must yield 501; got: {text}");
    }

    /// <summary>
    ///     Bug class: CancelRequest without a staged response still dials the H2 origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeRequest_CancelRequest_DoesNotContactOrigin()
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

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var owned = suite;
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.CancelRequest = true;
            e.GenericResponse("cancelled", HttpStatusCode.ServiceUnavailable);
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var response = await client.GetAsync(origin.Url);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.IsFalse(originContacted);
    }
}
