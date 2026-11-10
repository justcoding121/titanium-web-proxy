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
///     Fat <c>Http3OriginBridge.Quic</c> suite: pooled QUIC invalidate after a mid-connection failure, then recovery.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3OriginBridgeH3PoolRecoverFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    /// <summary>
    ///     Bug class: after origin stream failure the pool keeps a dead QUIC member and all later H3→H3 forwards 502.
    /// </summary>
    [TestMethod]
    [Timeout(90_000)]
    public async Task H3ToH3_FailedSecondRequest_ThirdRecovers()
    {
        RequireQuic();
        var hits = 0;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ =>
        {
            var n = Interlocked.Increment(ref hits);
            if (n == 2)
                throw new InvalidOperationException("poison-stream");
            return Task.FromResult(new QuicHttp3Response(200, "ok" + n));
        });

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        ep.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var proxy = new ProxyServer(false, false, false)
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
        proxy.AddEndPoint(ep);
        proxy.Start();

        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var first = await client.SendAsync("GET", $"localhost:{origin.Port}", "/a");
            Assert.AreEqual(200, first.StatusCode, first.TextBody);

            var second = await client.SendAsync("GET", $"localhost:{origin.Port}", "/b");
            Assert.IsTrue(second.StatusCode is >= 500 and < 600 || second.StatusCode == 0,
                $"Poisoned origin hop must fail; got {second.StatusCode}.");

            var third = await client.SendAsync("GET", $"localhost:{origin.Port}", "/c");
            Assert.AreEqual(200, third.StatusCode, third.TextBody);
            Assert.AreEqual("ok3", third.TextBody);
        }
        finally
        {
            proxy.Stop();
        }
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
