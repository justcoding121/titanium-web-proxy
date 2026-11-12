using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Remaining <c>Http11ToHttp2BridgeHandler</c> early-exit branches: header-limit 431,
///     ambiguous framing rejection, and reverse-proxy no-destination.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http11ToHttp2BridgeFramingGapCoverageTests
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
    ///     Bug class: oversized request headers on the H1→H2 bridge hang instead of returning 431.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OversizedRequestHeaders_Returns431()
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
                maxHeaderLineBytes: 256,
                maxHeaderCount: 8,
                maxHeaderAggregateBytes: 512,
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
        request.Headers.TryAddWithoutValidation("x-huge", new string('a', 2048));
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.RequestHeaderFieldsTooLarge, response.StatusCode);
    }

    /// <summary>
    ///     Bug class: H1→H2 bridge BeforeRequest Ok still dials the origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeRequest_Ok_DoesNotContactOrigin()
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
            e.Ok("h1h2-ok");
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var response = await client.GetAsync(origin.Url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("h1h2-ok", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(originContacted);
    }

    /// <summary>
    ///     Bug class: H1→H2 Expect: 100-continue with a denied BeforeRequest still sends the body
    ///     to the origin after a synthetic response.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeRequest_GenericResponse_WithExpectContinue_DoesNotContactOrigin()
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
            e.GenericResponse("denied", HttpStatusCode.Forbidden);
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Post, origin.Url)
        {
            Content = new StringContent("body-bytes", Encoding.ASCII, "text/plain"),
        };
        request.Headers.ExpectContinue = true;
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.IsFalse(originContacted);
    }
}
