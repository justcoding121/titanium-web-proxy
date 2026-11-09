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
///     Remaining <c>Http3RequestStream</c> pseudo-header arms: Via loop 508 and
///     <c>:protocol</c> on non-CONNECT requests.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3RequestStreamViaAndProtocolGapCoverageTests
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
    ///     Bug class: H3 Via loop is not detected and the origin is contacted.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_ViaLoop_Returns508()
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
            p.EnableHttpInterception = true;
            p.ViaHeaderPseudonym = "h3-via-loop-mark";
        });
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync(
                "GET",
                $"localhost:{origin.Port}",
                "/via-loop",
                extraRequestHeaders: [("via", "1.1 h3-via-loop-mark")]);
            Assert.AreEqual(508, response.StatusCode, response.TextBody);
            Assert.AreEqual(0, origin.AcceptedConnectionCount);
        }
        finally
        {
            proxy.Stop();
        }
    }

    /// <summary>
    ///     RFC 9114: <c>:protocol</c> is only valid on CONNECT. Bug class: accepted on GET
    ///     and forwarded to the origin.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_ProtocolOnGet_AbortsStreamWithoutOrigin()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "origin")));

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
            try
            {
                var response = await client.SendAsync(
                    "GET",
                    $"localhost:{origin.Port}",
                    "/proto-on-get",
                    extraRequestHeaders: [(":protocol", "webtransport")]);
                Assert.IsTrue(response.StatusCode is 400 or 500 or 501 or >= 502,
                    $"Non-CONNECT :protocol must fail; got {response.StatusCode}.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or QuicException)
            {
                // Stream reset before a response HEADERS frame — valid failure mode.
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
