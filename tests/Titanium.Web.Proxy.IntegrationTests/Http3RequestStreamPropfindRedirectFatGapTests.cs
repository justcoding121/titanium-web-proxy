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
///     Fat Http3RequestStream arms: PROPFIND, empty POST, BeforeRequest redirect,
///     and AfterResponse once.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3RequestStreamPropfindRedirectFatGapTests
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
    public async Task H3_Propfind_RoundTrip()
    {
        RequireQuic();
        string? seenMethod = null;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seenMethod = req.Method;
            return Task.FromResult(new QuicHttp3Response(207, "<multistatus/>"));
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
            var body = Encoding.UTF8.GetBytes("<propfind/>");
            var response = await client.SendAsync("PROPFIND", $"localhost:{origin.Port}", "/dav", body);
            Assert.AreEqual(207, response.StatusCode, response.TextBody);
            Assert.AreEqual("PROPFIND", seenMethod);
            Assert.AreEqual("<multistatus/>", response.TextBody);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_EmptyPost_RoundTrip()
    {
        RequireQuic();
        var seenLen = -1;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seenLen = req.Body.Length;
            return Task.FromResult(new QuicHttp3Response(204, ""));
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
            var response = await client.SendAsync("POST", $"localhost:{origin.Port}", "/empty",
                Array.Empty<byte>());
            Assert.AreEqual(204, response.StatusCode, response.TextBody);
            Assert.AreEqual(0, seenLen);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_BeforeRequest_Redirect_SkipsOrigin()
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
                e.Redirect("https://example.test/elsewhere");
                return Task.CompletedTask;
            };
        });
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/redir",
                Array.Empty<byte>());
            Assert.AreEqual(302, response.StatusCode, response.TextBody);
            Assert.IsFalse(contacted);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_AfterResponse_FiresOnce()
    {
        RequireQuic();
        var after = 0;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "ok")));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep, p =>
        {
            p.AfterResponse += (_, _) =>
            {
                System.Threading.Interlocked.Increment(ref after);
                return Task.CompletedTask;
            };
        });
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/after",
                Array.Empty<byte>());
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            for (var i = 0; i < 50 && System.Threading.Volatile.Read(ref after) == 0; i++)
                await Task.Delay(50);
            Assert.AreEqual(1, System.Threading.Volatile.Read(ref after));
        }
        finally
        {
            proxy.Stop();
        }
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
