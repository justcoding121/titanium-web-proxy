#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http3RequestStream</c> suite: proxy authorization challenge on QUIC clients.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3RequestStreamProxyAuth407FatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    /// <summary>
    ///     Bug class: HTTP/3 listener accepts anonymous requests when proxy auth is configured.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_ProxyAuthRequired_UnauthorizedClientGets407()
    {
        RequireQuic();

        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("must-not-reach"));

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = server.HttpsListeningPort,
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
        proxy.ProxyBasicAuthenticateFunc = (_, _, _) => Task.FromResult(false);
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
                "/protected");
            Assert.AreEqual(407, response.StatusCode, response.TextBody);
            Assert.IsTrue(
                response.ExtraHeaders?.Any(h => h.Name == "proxy-authenticate") == true,
                "407 must advertise Proxy-Authenticate.");
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    /// <summary>
    ///     Bug class: valid Proxy-Authorization on HTTP/3 still answers 407.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_ProxyAuthRequired_ValidCredential_ReachesOrigin()
    {
        RequireQuic();

        var reached = false;
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(ctx =>
        {
            reached = true;
            return ctx.Response.WriteAsync("authed-h3");
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = server.HttpsListeningPort,
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
        proxy.ProxyBasicAuthenticateFunc = (_, user, password) =>
            Task.FromResult(user == "user" && password == "pass");
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

        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes("user:pass"));

        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            var response = await client.SendAsync(
                "GET",
                $"localhost:{server.HttpsListeningPort}",
                "/protected",
                extraRequestHeaders: [("proxy-authorization", $"Basic {token}")]);
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("authed-h3", response.TextBody);
            Assert.IsTrue(reached);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
