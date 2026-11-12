#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     <c>Http3RequestStream</c> extended CONNECT pseudo-header validation arms.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3RequestStreamExtendedConnectGapCoverageTests
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

    /// <summary>
    ///     Bug class: CONNECT with <c>:protocol</c> is accepted when SETTINGS_ENABLE_CONNECT_PROTOCOL is off.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_ExtendedConnect_WithoutAdvertise_AbortsWithoutOrigin()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "origin")));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep, p =>
        {
            p.EnableRfc8441 = false;
        });
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            try
            {
                var response = await client.SendAsync(
                    "CONNECT",
                    $"localhost:{origin.Port}",
                    "/masque",
                    extraRequestHeaders: [(":protocol", "connect-udp")]);
                Assert.IsTrue(response.StatusCode is 400 or 500 or 501 or >= 502,
                    $"CONNECT protocol must fail without advertise; got {response.StatusCode}.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or QuicException)
            {
                // Stream reset — valid failure mode.
            }

            Assert.AreEqual(0, origin.AcceptedConnectionCount);
        }
        finally
        {
            proxy.Stop();
        }
    }

    /// <summary>
    ///     Bug class: CONNECT with an unsupported <c>:protocol</c> still opens an origin hop.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_ExtendedConnect_UnsupportedProtocol_Returns501WithoutOrigin()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "origin")));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep, p => p.EnableRfc8441 = true);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync(
                "CONNECT",
                $"localhost:{origin.Port}",
                "/ec-bad",
                extraRequestHeaders: [(":protocol", "connect-ip")]);
            Assert.AreEqual(501, response.StatusCode, response.TextBody);
            Assert.AreEqual(0, origin.AcceptedConnectionCount);
        }
        finally
        {
            proxy.Stop();
        }
    }

    /// <summary>
    ///     Bug class: extended CONNECT with empty <c>:path</c> is forwarded to the origin.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_ExtendedConnect_EmptyPath_AbortsWithoutOrigin()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "origin")));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep, p => p.EnableRfc8441 = true);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            try
            {
                var response = await client.SendAsync(
                    "CONNECT",
                    $"localhost:{origin.Port}",
                    "",
                    extraRequestHeaders: [(":protocol", "webtransport")]);
                Assert.IsTrue(response.StatusCode is 400 or 500 or 501 or >= 502,
                    $"Empty :path on extended CONNECT must fail; got {response.StatusCode}.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or QuicException)
            {
            }

            Assert.AreEqual(0, origin.AcceptedConnectionCount);
        }
        finally
        {
            proxy.Stop();
        }
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
