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
///     Fat <c>Http3OriginBridge</c> H3→H1 TCP fast path: request headers survive the bridge.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3OriginBridgeH1AcceptDatetimeFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH1_AcceptDatetime_ReachesOrigin()
    {
        RequireQuic();

        string? seen = null;
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(ctx =>
        {
            if (ctx.Request.Headers.TryGetValue("Accept-Datetime", out var v))
                seen = v.ToString();
            return ctx.Response.WriteAsync("h3-h1-adt");
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = server.HttpsListeningPort,
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
            var response = await client.SendAsync(
                "GET",
                $"localhost:{server.HttpsListeningPort}",
                "/adt",
                extraRequestHeaders: [("accept-datetime", "Mon, 09 Nov 2026 16:00:00 GMT")]);
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("h3-h1-adt", response.TextBody);
            Assert.IsTrue(seen?.Contains("Nov 2026", StringComparison.Ordinal), seen);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH1_UpgradeInsecureRequests_ReachesOrigin()
    {
        RequireQuic();

        string? seen = null;
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(ctx =>
        {
            if (ctx.Request.Headers.TryGetValue("Upgrade-Insecure-Requests", out var v))
                seen = v.ToString();
            return ctx.Response.WriteAsync("h3-h1-uisr");
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = server.HttpsListeningPort,
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
            var response = await client.SendAsync(
                "GET",
                $"localhost:{server.HttpsListeningPort}",
                "/uisr",
                extraRequestHeaders: [("upgrade-insecure-requests", "1")]);
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("h3-h1-uisr", response.TextBody);
            Assert.AreEqual("1", seen);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
