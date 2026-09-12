using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression for Chrome/Edge <c>net::ERR_HTTP2_PROTOCOL_ERROR</c> (unexpected EOF during MITM
///     ServerHello) when a cold origin HTTP/2 ALPN probe is slower than the browser's handshake wait.
///     ServerHello must proceed inside <c>Http2ServerHelloProbeBudget</c>; the probe continues and is
///     adopted as the session connection (no extra prefetch origin handshake).
/// </summary>
[TestClass]
public class Http2ServerHelloProbeBudgetIntegrationTests
{
    private static readonly TimeSpan SlowOriginHandshake = TimeSpan.FromSeconds(3);

    private static X509Certificate2 CreateOriginCertificate()
    {
        return TestCertificateAuthority.ServerCertificate;
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task SlowOriginProbe_DoesNotBlockMitmServerHello_AndAdoptsTheProbeConnection()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate(), SlowOriginHandshake);
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();

            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, true);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableTcpServerConnectionPrefetch = true;
        ((Titanium.Web.Proxy.Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0]).BeforeTunnelConnectRequest +=
            (_, e) =>
            {
                e.AllowHttpProtocolTranslation = true;
                return Task.CompletedTask;
            };

        var uri = new Uri(rawServer.Url);
        var alpn = new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 };

        // Isolate ServerHello latency from first-leaf BouncyCastle cost.
        var certName = Titanium.Web.Proxy.Helpers.HttpHelper.GetWildCardDomainName(
            uri.Host, proxy.CertificateManager.DisableWildCardCertificates);
        Assert.IsNotNull(await proxy.CertificateManager.CreateServerCertificate(certName));

        var sw = Stopwatch.StartNew();
        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(
            proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port, alpn);
        sw.Stop();

        Assert.AreEqual(SslApplicationProtocol.Http2, tunnel.NegotiatedApplicationProtocol,
            "Speculative client ALPN must still offer h2 while the cold origin probe is in flight.");
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(1),
            $"MITM ServerHello took {sw.Elapsed}; expected inside Http2ServerHelloProbeBudget plus local TLS/cert work, not the {SlowOriginHandshake} origin probe.");
        Assert.IsTrue(sw.Elapsed < SlowOriginHandshake - TimeSpan.FromSeconds(1),
            $"MITM ServerHello took {sw.Elapsed}; it must not wait for the {SlowOriginHandshake} origin TLS probe.");

        var h2 = await tunnel.StartHttp2Async();
        var requestHeaders = h2.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            Array.Empty<(string, string)>());
        await h2.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (_, responseHeaders, _) = await h2.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value,
            "The deferred origin probe must still complete and serve the first request.");

        Assert.AreEqual(1, rawServer.AcceptedConnectionCount,
            "Deferred cold probe must be adopted as the session connection; prefetch must not open a second origin TLS handshake (RPS/connection regression).");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task WarmCacheAfterSlowProbe_ServerHelloStaysFast_AndDoesNotReprobe()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate(), SlowOriginHandshake);
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();

            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, true);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableTcpServerConnectionPrefetch = true;
        ((Titanium.Web.Proxy.Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0]).BeforeTunnelConnectRequest +=
            (_, e) =>
            {
                e.AllowHttpProtocolTranslation = true;
                return Task.CompletedTask;
            };

        var uri = new Uri(rawServer.Url);
        var alpn = new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 };

        using (var first = await Http2RawClient.ConnectTunnelWithAlpnAsync(
                   proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port, alpn))
        {
            var h2 = await first.StartHttp2Async();
            var requestHeaders = h2.EncodeHeaders(
                new[]
                {
                    (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                    (":path", "/")
                },
                Array.Empty<(string, string)>());
            await h2.WriteHeaderBlockAsync(1, requestHeaders, true);
            var (_, responseHeaders, _) = await h2.ReadHeaderBlockAsync();
            Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
        }

        var connectionsAfterFirst = rawServer.AcceptedConnectionCount;

        var sw = Stopwatch.StartNew();
        using var second = await Http2RawClient.ConnectTunnelWithAlpnAsync(
            proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port, alpn);
        sw.Stop();

        Assert.AreEqual(SslApplicationProtocol.Http2, second.NegotiatedApplicationProtocol);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(1),
            $"Warm-cache ServerHello took {sw.Elapsed}; a cache hit must not wait on origin TLS.");

        // Prefetch is on: the second tunnel may open its session connection during/after ServerHello.
        // It must not open an extra capability probe on top of that (would be first+2).
        var h2Second = await second.StartHttp2Async();
        var secondRequest = h2Second.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            Array.Empty<(string, string)>());
        await h2Second.WriteHeaderBlockAsync(1, secondRequest, true);
        var (_, secondHeaders, _) = await h2Second.ReadHeaderBlockAsync();
        Assert.AreEqual("200", secondHeaders.Single(h => h.Name == ":status").Value);

        Assert.AreEqual(connectionsAfterFirst + 1, rawServer.AcceptedConnectionCount,
            "Warm cache must not run another HTTP/2 capability probe; only the session connection is new.");
    }
}
