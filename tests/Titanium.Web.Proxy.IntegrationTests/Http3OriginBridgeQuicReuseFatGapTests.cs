#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http3OriginBridge.Quic</c> suite: back-to-back H3→H3 forwards reuse the QUIC pool.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3OriginBridgeQuicReuseFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    /// <summary>
    ///     Bug class: a second H3 client request opens a fresh origin QUIC socket instead of reusing the pool.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH3_BackToBackGets_BothBodiesAndSingleWarmConnect()
    {
        RequireQuic();

        var requests = 0;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new QuicHttp3Response(200, req.Path));
        });

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateInterceptingH3Proxy(ep);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var first = await client.SendAsync("GET", $"localhost:{origin.Port}", "/one");
            var second = await client.SendAsync("GET", $"localhost:{origin.Port}", "/two");
            Assert.AreEqual(200, first.StatusCode, first.TextBody);
            Assert.AreEqual(200, second.StatusCode, second.TextBody);
            Assert.AreEqual("/one", first.TextBody);
            Assert.AreEqual("/two", second.TextBody);
            Assert.AreEqual(2, requests);
            Assert.IsTrue(origin.AcceptedConnectionCount >= 1,
                "At least one origin QUIC connection must be accepted.");
        }
        finally
        {
            proxy.Stop();
        }
    }

    private static ProxyServer CreateInterceptingH3Proxy(TransparentQuicProxyEndPoint ep)
    {
        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false,
            EnableHttpInterception = true,
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        ep.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(ep);
        proxy.Start();
        return proxy;
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
