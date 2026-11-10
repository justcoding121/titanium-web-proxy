#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http3OriginBridge</c> suite: stale H3 capability with live TCP origin must fall back over HTTP/1.1.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3OriginBridgeAutoQuicMissTcpOkFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    /// <summary>
    ///     Bug class: Auto policy wedges when UDP is dead but TCP answers on the same host:port.
    /// </summary>
    [TestMethod]
    [Timeout(90_000)]
    public async Task H3Client_StaleH3Capability_DeadUdp_LiveTcp_Returns200()
    {
        RequireQuic();
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("tcp-fallback-ok"));

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = server.HttpsListeningPort,
        };
        quicEp.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Auto;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

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

        var hostAndPort = $"localhost:{server.HttpsListeningPort}";
        proxy.Http3OriginCapabilityCache.Set(hostAndPort, int.MinValue, TimeSpan.FromMinutes(5), targetName: null);
        proxy.Http3WarmOrigins.Mark("localhost", server.HttpsListeningPort);

        proxy.AddEndPoint(quicEp);
        proxy.Start();
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            var response = await client.SendAsync(
                "GET", $"localhost:{server.HttpsListeningPort}", "/auto-fallback");
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("tcp-fallback-ok", response.TextBody);
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
