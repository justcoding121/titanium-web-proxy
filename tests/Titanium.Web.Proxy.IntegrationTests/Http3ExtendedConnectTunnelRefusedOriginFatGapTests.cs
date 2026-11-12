#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http3ExtendedConnectTunnel</c> suite: connection refused on WebSocket upgrade is not 403.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3ExtendedConnectTunnelRefusedOriginFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static int RefusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    ///     Bug class: refused TCP origin on extended CONNECT is mis-reported as 403 Forbidden.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_ExtendedConnect_RefusedOrigin_Returns502Not403()
    {
        RequireQuic();
        var dead = RefusedPort();
        using var testSuite = new TestSuite();
        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = dead,
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
            EnableHttpsSvcbDnsDiscovery = false,
            EnableRfc8441 = true,
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.AddEndPoint(quicEp);
        proxy.Start();
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();
            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{dead}", "/ws", "websocket", scheme: "http");
            Assert.AreNotEqual(403, tunnel.Status, "Refused origin must not masquerade as 403.");
            Assert.IsTrue(tunnel.Status is >= 500 and < 600, $"Expected 5xx; got {tunnel.Status}.");
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
