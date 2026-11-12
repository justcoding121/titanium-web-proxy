#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http3OriginBridge</c> H3→H2 fast path: Vary response header reaches the QUIC client.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3OriginBridgeH2VaryFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    /// <summary>
    ///     Bug class: H2 origin Vary is dropped on the H3 front door.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH2_Vary_ReachesClient()
    {
        RequireQuic();

        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        var uri = new Uri(origin.Url);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders(
                    [
                        (":status", "200"),
                        ("vary", "Accept-Language"),
                        ("content-length", "3"),
                    ], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, "h2v"u8.ToArray());
            }
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = uri.Host,
            ForwardPort = uri.Port,
        };
        quicEp.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
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
                $"{uri.Host}:{uri.Port}",
                "/vary-h2");
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            var vary = response.ExtraHeaders?.FirstOrDefault(h => h.Name == "vary").Value;
            Assert.IsNotNull(vary);
            Assert.IsTrue(vary!.Contains("Accept-Language", StringComparison.OrdinalIgnoreCase), vary);
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
