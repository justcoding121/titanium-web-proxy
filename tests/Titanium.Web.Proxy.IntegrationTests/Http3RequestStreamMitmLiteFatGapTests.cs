#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
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
///     Remaining <c>Http3RequestStream</c> MITM unchanged-lite arms (H3→H1/H2/H3),
///     interim 1xx relay, streamed request hooks, and trailer HEADERS on responses.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3RequestStreamMitmLiteFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static ProxyServer CreateInterceptingH3Proxy(
        TransparentQuicProxyEndPoint ep,
        UpstreamHttpProtocol upstream,
        Action<ProxyServer>? configure = null)
    {
        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false,
            EnableHttpInterception = true,
            ViaHeaderPseudonym = "h3-mitm-lite",
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        ep.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = upstream;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        configure?.Invoke(proxy);
        proxy.AddEndPoint(ep);
        proxy.Start();
        return proxy;
    }

    /// <summary>
    ///     Bug class: MITM unchanged H3→H1 skips FinishMitmPreencoded and never reaches the origin.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_MitmUnchanged_H1_RoundTrips()
    {
        RequireQuic();
        var body = "h3-h1-lite";
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync(body));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = server.HttpsListeningPort,
        };
        using var proxy = CreateInterceptingH3Proxy(ep, UpstreamHttpProtocol.Http11);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync(
                "GET", $"localhost:{server.HttpsListeningPort}", "/lite-h1", Array.Empty<byte>());
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual(body, response.TextBody);
        }
        finally
        {
            proxy.Stop();
        }
    }

    /// <summary>
    ///     Bug class: MITM unchanged H3→H3 does not capture static QPACK for FinishMitmPreencoded.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_MitmUnchanged_H3_RoundTrips()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "h3-h3-lite")));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateInterceptingH3Proxy(ep, UpstreamHttpProtocol.Http3);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/lite-h3", Array.Empty<byte>());
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("h3-h3-lite", response.TextBody);
        }
        finally
        {
            proxy.Stop();
        }
    }

    /// <summary>
    ///     Bug class: MITM unchanged H3→H2 does not use ForwardOverHttp2FastAsync.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_MitmUnchanged_H2_RoundTrips()
    {
        RequireQuic();
        using var h2Origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        h2Origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadHeaderBlockAsync();
            var block = conn.EncodeHeaders(
                [(":status", "200")],
                [("content-type", "text/plain"), ("content-length", "11")]);
            await conn.WriteHeaderBlockAsync(streamId, block, false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId,
                Http2FrameFlag.EndStream, "h3-h2-lite"u8.ToArray());
        });

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = h2Origin.Port,
        };
        using var proxy = CreateInterceptingH3Proxy(ep, UpstreamHttpProtocol.Http2,
            p => p.EnableHttp2 = true);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync("GET", $"localhost:{h2Origin.Port}", "/lite-h2", Array.Empty<byte>());
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("h3-h2-lite", response.TextBody);
        }
        finally
        {
            proxy.Stop();
        }
    }

    /// <summary>
    ///     Bug class: OnRequestBodyWrite on H3 does not stream chunks to the origin.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_OnRequestBodyWrite_ForwardsReplacedChunks()
    {
        RequireQuic();
        byte[]? seen = null;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seen = req.Body;
            return Task.FromResult(new QuicHttp3Response(200, $"n={req.Body.Length}"));
        });

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateInterceptingH3Proxy(ep, UpstreamHttpProtocol.Http3, p =>
        {
            p.OnRequestBodyWrite += (_, e) =>
            {
                e.BodyBytes = Encoding.UTF8.GetBytes("rewritten");
                return Task.CompletedTask;
            };
        });
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync(
                "POST", $"localhost:{origin.Port}", "/hook", Encoding.UTF8.GetBytes("original"));
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("rewritten"), seen);
        }
        finally
        {
            proxy.Stop();
        }
    }

    /// <summary>
    ///     Bug class: GetRequestBody during BeforeRequest on H3 does not buffer client DATA.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_GetRequestBody_InBeforeRequest_RoundTrips()
    {
        RequireQuic();
        byte[]? fromHandler = null;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
            Task.FromResult(new QuicHttp3Response(200, $"n={req.Body.Length}")));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateInterceptingH3Proxy(ep, UpstreamHttpProtocol.Http3, p =>
        {
            p.BeforeRequest += async (_, e) =>
            {
                fromHandler = await e.GetRequestBody();
            };
        });
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var payload = Encoding.UTF8.GetBytes("buffer-me");
            var response = await client.SendAsync(
                "POST", $"localhost:{origin.Port}", "/buf", payload);
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            CollectionAssert.AreEqual(payload, fromHandler);
        }
        finally
        {
            proxy.Stop();
        }
    }

    /// <summary>
    ///     Bug class: large unchanged MITM H3→H1 responses fail to coalesce HEADERS+DATA.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_MitmUnchanged_H1_LargeBody_RoundTrips()
    {
        RequireQuic();
        var payload = new string('L', 18 * 1024);
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync(payload));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = server.HttpsListeningPort,
        };
        using var proxy = CreateInterceptingH3Proxy(ep, UpstreamHttpProtocol.Http11);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync(
                "GET", $"localhost:{server.HttpsListeningPort}", "/big", Array.Empty<byte>());
            Assert.AreEqual(200, response.StatusCode);
            Assert.AreEqual(payload.Length, response.TextBody.Length);
            Assert.AreEqual(payload, response.TextBody);
        }
        finally
        {
            proxy.Stop();
        }
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
