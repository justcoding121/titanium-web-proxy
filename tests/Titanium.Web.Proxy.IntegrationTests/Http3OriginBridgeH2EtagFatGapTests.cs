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
///     Fat <c>Http3OriginBridge</c> H3→H2 fast path: ETag response header reaches the QUIC client.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3OriginBridgeH2EtagFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    /// <summary>
    ///     Bug class: ETag from the H2 origin is stripped on the H3 front door.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3ToH2_Etag_ReachesClient()
    {
        RequireQuic();

        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders(
                    [
                        (":status", "200"),
                        ("etag", "\"h2-bridge-gap\""),
                    ],
                    [("content-type", "text/plain"), ("content-length", "3")]);
                await conn.WriteHeaderBlockAsync(streamId, resp, false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                    System.Text.Encoding.UTF8.GetBytes("ok\n"));
            }
        });

        var uri = new Uri(origin.Url);
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
                "/etag-h2");
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            var etag = response.ExtraHeaders?.FirstOrDefault(h => h.Name == "etag").Value;
            Assert.IsNotNull(etag);
            Assert.IsTrue(etag!.Contains("h2-bridge-gap", StringComparison.Ordinal), etag);
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
