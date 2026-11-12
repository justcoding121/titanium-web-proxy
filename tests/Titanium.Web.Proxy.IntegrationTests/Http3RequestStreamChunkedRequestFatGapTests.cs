#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http3RequestStream</c> arms: multi-frame client DATA and multi-frame origin
///     responses through transparent H3 forwarding.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3RequestStreamChunkedRequestFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static ProxyServer CreateH3Proxy(TransparentQuicProxyEndPoint ep, Action<ProxyServer>? configure = null)
    {
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
        ep.BeforeQuicAuthenticate += async (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            await Task.CompletedTask;
        };
        configure?.Invoke(proxy);
        proxy.AddEndPoint(ep);
        proxy.Start();
        return proxy;
    }

    /// <summary>
    ///     Bug class: chunked H3 request DATA is not coalesced before the origin handler runs.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_MultiFrameRequestBody_RoundTrips()
    {
        RequireQuic();
        int frames = 0;
        byte[]? seen = null;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            frames = req.DataFrameCount;
            seen = req.Body;
            return Task.FromResult(new QuicHttp3Response(200, $"f={req.DataFrameCount}"));
        });

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var body = Encoding.UTF8.GetBytes(new string('B', 48));
            var response = await client.SendAsync(
                "POST", $"localhost:{origin.Port}", "/chunks", body, requestDataFrameSize: 16);
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("f=3", response.TextBody);
            Assert.IsTrue(frames >= 2, "Origin must observe multiple request DATA frames.");
            CollectionAssert.AreEqual(body, seen);
        }
        finally
        {
            proxy.Stop();
        }
    }

    /// <summary>
    ///     Bug class: large multi-frame H3 responses are truncated on the client.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_MultiFrameResponseBody_RoundTrips()
    {
        RequireQuic();
        var payload = new string('R', 48);
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ =>
            Task.FromResult(new QuicHttp3Response(
                200, payload, Encoding.UTF8.GetBytes(payload), dataFrameSize: 16)));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync(
                "GET", $"localhost:{origin.Port}", "/big", Array.Empty<byte>());
            Assert.AreEqual(200, response.StatusCode);
            Assert.AreEqual(payload, response.TextBody);
        }
        finally
        {
            proxy.Stop();
        }
    }
}

#pragma warning restore TWP001
#pragma warning restore CA1416
