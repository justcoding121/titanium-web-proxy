#pragma warning disable CA1416
#pragma warning disable TWP001

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
///     Fat <c>Http3RequestStream</c> bodiless fast path: HEAD must not upload DATA to the origin.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3RequestStreamHeadNoBodyFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    /// <summary>
    ///     Bug class: HEAD over H3→H1 sends a request body or leaves the origin waiting for DATA.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH1_Head_NoRequestBody_OriginAnswers()
    {
        RequireQuic();

        var method = "POST";
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(ctx =>
        {
            method = ctx.Request.Method;
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength = 0;
            return Task.CompletedTask;
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = server.HttpListeningPort,
            ForwardCleartext = true,
        };
        quicEp.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
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
        proxy.AddEndPoint(quicEp);
        proxy.Start();

        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            var response = await client.SendAsync(
                "HEAD",
                $"127.0.0.1:{server.HttpListeningPort}",
                "/head");
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("HEAD", method);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
