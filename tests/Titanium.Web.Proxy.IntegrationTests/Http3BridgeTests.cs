#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Cross-version HTTP/3 bridge acceptance tests (H1↔H3, H3→H1).
/// </summary>
[TestClass]
public class Http3BridgeTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    [TestMethod]
    public async Task Http11Client_ForcedHttp3Origin_DeliversResponse()
    {
        RequireQuic();

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            Assert.AreEqual("GET", req.Method);
            return Task.FromResult(new QuicHttp3Response(200, "h1-to-h3"));
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;
        proxy.BeforeRequest += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync($"https://localhost:{origin.Port}/via-h3");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("h1-to-h3", body);
        Assert.AreEqual(new Version(1, 1), response.Version);
        Assert.IsTrue(origin.AcceptedConnectionCount >= 1);
    }

    [TestMethod]
    public async Task Http11Client_ForcedHttp3Origin_PostBodyRoundTrips()
    {
        RequireQuic();

        var payload = Encoding.UTF8.GetBytes("bridge-post-body");
        byte[]? seen = null;

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seen = req.Body;
            return Task.FromResult(new QuicHttp3Response(200, $"got={req.Body.Length}"));
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;
        proxy.BeforeRequest += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);
        using var content = new ByteArrayContent(payload);
        var response = await client.PostAsync($"https://localhost:{origin.Port}/post", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual($"got={payload.Length}", body);
        CollectionAssert.AreEqual(payload, seen);
    }

    [TestMethod]
    public async Task Http3Client_ForcedHttp11Origin_UsesTcpFallback()
    {
        RequireQuic();

        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(async ctx =>
        {
            await ctx.Response.WriteAsync("h3-to-h1");
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = server.HttpsListeningPort
        };
        quicEp.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(quicEp);
        proxy.Start();

        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");

            // Authority must include the Kestrel HTTPS port — H3→TCP uses RequestUri, not ForwardPort.
            var response = await client.SendAsync("GET", $"localhost:{server.HttpsListeningPort}", "/tcp");
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("h3-to-h1", response.TextBody);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    public async Task Http2Client_WarmHttp3Origin_BridgesSuccessfully()
    {
        RequireQuic();

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "h2-to-h3")));

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;

        // Seed capability + warm registry so Auto policy routes to H3 without waiting for Alt-Svc.
        var hostAndPort = $"localhost:{origin.Port}";
        proxy.Http3OriginCapabilityCache.Set(hostAndPort, int.MinValue, TimeSpan.FromMinutes(5), targetName: null);
        proxy.Http3WarmOrigins.Mark("localhost", origin.Port);

        proxy.BeforeRequest += (_, args) =>
        {
            // Keep Auto so ResolveHttp3Origin uses the warm capability cache entry.
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Auto;
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        var response = await client.GetAsync($"https://localhost:{origin.Port}/h2bridge");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("h2-to-h3", body);
        Assert.IsTrue(origin.AcceptedConnectionCount >= 1);
    }

    [TestMethod]
    public async Task Http2Client_ColdForcedHttp3_BridgesToQuicOrigin()
    {
        RequireQuic();

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "h2-cold-h3")));

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            // Force H3 at CONNECT so SendHttp2ToHttp3Bridge runs with NullOriginStream (cold path).
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        var response = await client.GetAsync($"https://localhost:{origin.Port}/cold");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("h2-cold-h3", body);
        Assert.IsTrue(origin.AcceptedConnectionCount >= 1);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2Client_ColdForcedHttp3_LargeBody_ExceedsDefaultStreamWindow()
    {
        RequireQuic();

        // Larger than the RFC default 65535-byte stream send window. Synthetic/bridge streams must
        // still apply client stream WINDOW_UPDATE (and reserve credit outside the write lock) or
        // HttpClient stalls once the initial window is exhausted.
        var expectedBody = new string('z', 200_000);
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, expectedBody)));

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        client.Timeout = TimeSpan.FromSeconds(25);
        var response = await client.GetAsync($"https://localhost:{origin.Port}/large");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual(expectedBody.Length, body.Length);
        Assert.AreEqual(expectedBody, body);
    }

    [TestMethod]
    public async Task Http2Client_ColdH3Miss_FallsBackToTcpWithoutHang()
    {
        RequireQuic();

        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(async ctx =>
        {
            await ctx.Response.WriteAsync("cold-tcp-fallback");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            // Enter the cold NullOriginStream H2→H3 bridge at CONNECT time.
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        proxy.BeforeRequest += (_, args) =>
        {
            // Per-stream override: leave the cold bridge but force TCP so !UseH3 must self-fallback
            // instead of writing into NullOriginStream (which would hang the client).
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        var response = await client.GetAsync($"https://localhost:{server.HttpsListeningPort}/fallback");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("cold-tcp-fallback", body);
    }

    [TestMethod]
    public async Task Http11Client_ForcedHttp3_UnreachableOrigin_Returns502()
    {
        RequireQuic();

        // Bind then close so the port is almost certainly closed when the proxy dials QUIC.
        int closedPort;
        await using (var ephemeral = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate))
            closedPort = ephemeral.Port;

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;
        proxy.BeforeRequest += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync($"https://localhost:{closedPort}/gone");
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [TestMethod]
    public async Task Http11Client_ForcedHttp3Origin_GzipBody_SurvivesBridgeWithoutCorruption()
    {
        RequireQuic();

        var plain = Encoding.UTF8.GetBytes("gzip-plain");
        byte[] gzipped;
        using (var ms = new System.IO.MemoryStream())
        {
            using (var gzip = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
                gzip.Write(plain);
            gzipped = ms.ToArray();
        }

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(
            200, "gzip-plain", gzipped,
            extraHeaders: new List<(string, string)> { ("content-encoding", "gzip") })));

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;
        proxy.BeforeRequest += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            return Task.CompletedTask;
        };

        // AutomaticDecompression exercises the bridge's decompress-then-recompress path:
        // H3OriginBridge decompresses wire bytes so CompressBodyAndUpdateContentLength can
        // safely re-apply Content-Encoding for the H1 client without double-compressing.
        using var handler = new HttpClientHandler
        {
            Proxy = new TestHelper.TestProxy($"http://localhost:{proxy.ProxyEndPoints[0].Port}", false),
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.GZip,
            ServerCertificateCustomValidationCallback =
                (_, certificate, _, errors) => TestCertificateAuthority.Validate(certificate, errors)
        };
        using var client = new HttpClient(handler);
        var response = await client.GetAsync($"https://localhost:{origin.Port}/gzip");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("gzip-plain", body);
    }
}

#pragma warning restore TWP001
#pragma warning restore CA1416
