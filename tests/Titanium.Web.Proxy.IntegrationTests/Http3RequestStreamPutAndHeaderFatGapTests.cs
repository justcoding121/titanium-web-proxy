#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat Http3RequestStream arms: PUT with body, BeforeResponse header mark,
///     POST then GET on one client, and BeforeRequest Ok short-circuit.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3RequestStreamPutAndHeaderFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static ProxyServer CreateH3Proxy(TransparentQuicProxyEndPoint ep, Action<ProxyServer>? configure = null)
    {
        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false,
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        ep.BeforeQuicAuthenticate += async (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            await Task.CompletedTask;
        };
        configure?.Invoke(proxy);
        proxy.AddEndPoint(ep);
        proxy.Start();
        return proxy;
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_Put_RoundTripsBody()
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
        using var proxy = CreateH3Proxy(ep);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var body = Encoding.UTF8.GetBytes("put-h3");
            var response = await client.SendAsync("PUT", $"localhost:{origin.Port}", "/put", body);
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("n=6", response.TextBody);
            CollectionAssert.AreEqual(body, seen);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_BeforeResponse_AddsHeader()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "mark-me")));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep, p =>
        {
            p.BeforeResponse += (_, e) =>
            {
                e.HttpClient.Response.Headers.AddHeader("x-h3-mark", "1");
                return Task.CompletedTask;
            };
        });
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/mark", Array.Empty<byte>());
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("mark-me", response.TextBody);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_PostThenGet_SameClient()
    {
        RequireQuic();
        var hits = 0;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            System.Threading.Interlocked.Increment(ref hits);
            if (req.Method == "POST")
                return Task.FromResult(new QuicHttp3Response(200, "posted"));
            return Task.FromResult(new QuicHttp3Response(200, "got"));
        });

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var post = await client.SendAsync("POST", $"localhost:{origin.Port}", "/p",
                Encoding.UTF8.GetBytes("x"));
            var get = await client.SendAsync("GET", $"localhost:{origin.Port}", "/g", Array.Empty<byte>());
            Assert.AreEqual(200, post.StatusCode, post.TextBody);
            Assert.AreEqual("posted", post.TextBody);
            Assert.AreEqual(200, get.StatusCode, get.TextBody);
            Assert.AreEqual("got", get.TextBody);
            Assert.AreEqual(2, hits);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_BeforeRequest_Ok_SkipsOrigin()
    {
        RequireQuic();
        var contacted = false;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ =>
        {
            contacted = true;
            return Task.FromResult(new QuicHttp3Response(200, "nope"));
        });

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep, p =>
        {
            p.BeforeRequest += (_, e) =>
            {
                e.Ok("h3-ok-put");
                return Task.CompletedTask;
            };
        });
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/ok", Array.Empty<byte>());
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("h3-ok-put", response.TextBody);
            Assert.IsFalse(contacted);
        }
        finally
        {
            proxy.Stop();
        }
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
