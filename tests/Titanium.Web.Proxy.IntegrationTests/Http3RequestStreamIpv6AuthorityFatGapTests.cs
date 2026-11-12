#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http3RequestStream</c> suite: bracketed IPv6 <c>:authority</c> parsing for interception context.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3RequestStreamIpv6AuthorityFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_BracketedIpv6Authority_Get_ReachesOrigin()
    {
        RequireQuic();

        string? seenAuthority = null;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seenAuthority = req.Authority;
            return Task.FromResult(new QuicHttp3Response(204, ""));
        });

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "::1",
            ForwardPort = origin.Port,
        };
        ep.BeforeQuicAuthenticate += async (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            await Task.CompletedTask;
        };

        using var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpInterception = true,
            EnableHttpsSvcbDnsDiscovery = false,
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            var errors = args.SslPolicyErrors & ~SslPolicyErrors.RemoteCertificateNameMismatch;
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, errors);
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(ep);
        proxy.Start();

        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var authority = $"[::1]:{origin.Port}";
            var response = await client.SendAsync("GET", authority, "/v6");
            Assert.AreEqual(204, response.StatusCode, response.TextBody);
            StringAssert.Contains(seenAuthority, "::1");
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
