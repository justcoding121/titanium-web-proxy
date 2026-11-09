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
///     Fat Http3RequestStream arms: Allow on GET, PATCH with body, and HEAD
///     round-trip through transparent H3 forwarding.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3RequestStreamOptionsPatchTraceFatGapTests
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
    public async Task H3_Allow_OnGet_ReturnsHeader()
    {
        RequireQuic();
        string? seen = null;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seen = req.Method;
            return Task.FromResult(new QuicHttp3Response(200, "", Array.Empty<byte>(),
                extraHeaders: [("allow", "GET, OPTIONS")]));
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
            var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/allow",
                Array.Empty<byte>());
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("GET", seen);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_Patch_RoundTripsBody()
    {
        RequireQuic();
        byte[]? seen = null;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seen = req.Body;
            return Task.FromResult(new QuicHttp3Response(200, "patched"));
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
            var body = Encoding.UTF8.GetBytes("{\"op\":\"merge\"}");
            var response = await client.SendAsync("PATCH", $"localhost:{origin.Port}", "/res", body);
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("patched", response.TextBody);
            CollectionAssert.AreEqual(body, seen);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_Head_RoundTrip()
    {
        RequireQuic();
        string? seenMethod = null;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seenMethod = req.Method;
            return Task.FromResult(new QuicHttp3Response(200, "", Array.Empty<byte>()));
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
            var response = await client.SendAsync("HEAD", $"localhost:{origin.Port}", "/gap",
                Array.Empty<byte>());
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("HEAD", seenMethod);
        }
        finally
        {
            proxy.Stop();
        }
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
