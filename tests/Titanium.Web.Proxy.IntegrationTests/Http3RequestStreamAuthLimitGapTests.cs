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
///     Remaining <c>Http3RequestStream</c> arms: Basic auth 407, BeforeRequest Ok,
///     Via loop 508, and buffered-body 413 under MaxBufferedBodyBytes.
/// </summary>
[TestClass]
[DoNotParallelize]
public class Http3RequestStreamAuthLimitGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static ProxyServer CreateHttp3Proxy(TransparentQuicProxyEndPoint quicEndPoint,
        Action<ProxyServer>? configure = null)
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
        quicEndPoint.BeforeQuicAuthenticate += async (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            await Task.CompletedTask;
        };
        configure?.Invoke(proxy);
        proxy.AddEndPoint(quicEndPoint);
        proxy.Start();
        return proxy;
    }

    /// <summary>
    ///     Bug class: ProxyBasicAuthenticateFunc reject on H3 does not send 407 and leaves the stream open.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_BasicAuthReject_Returns407()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "should-not")));

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateHttp3Proxy(quicEp, p =>
        {
            p.ProxyBasicAuthenticateFunc = (_, _, _) => Task.FromResult(false);
        });

        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
        var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/auth");
        Assert.AreEqual(407, response.StatusCode, response.TextBody);
        Assert.AreEqual(0, origin.AcceptedConnectionCount);
    }

    /// <summary>
    ///     Bug class: BeforeRequest Ok still dials the H3 origin.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_BeforeRequest_Ok_DoesNotContactOrigin()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "origin")));

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateHttp3Proxy(quicEp, p =>
        {
            p.BeforeRequest += (_, e) =>
            {
                e.Ok("h3-ok");
                return Task.CompletedTask;
            };
        });

        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
        var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/ok");
        Assert.AreEqual(200, response.StatusCode, response.TextBody);
        Assert.AreEqual("h3-ok", response.TextBody);
        Assert.AreEqual(0, origin.AcceptedConnectionCount);
    }

    /// <summary>
    ///     Bug class: CancelRequest + GenericResponse still dials the H3 origin.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_BeforeRequest_CancelWithGenericResponse_DoesNotContactOrigin()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "origin")));

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateHttp3Proxy(quicEp, p =>
        {
            p.BeforeRequest += (_, e) =>
            {
                e.HttpClient.Request.CancelRequest = true;
                e.GenericResponse("h3-denied", HttpStatusCode.Forbidden);
                return Task.CompletedTask;
            };
        });

        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
        var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/deny");
        Assert.AreEqual(403, response.StatusCode, response.TextBody);
        Assert.AreEqual("h3-denied", response.TextBody);
        Assert.AreEqual(0, origin.AcceptedConnectionCount);
    }

    /// <summary>
    ///     Bug class: GetRequestBody during BeforeRequest that exceeds MaxBufferedBodyBytes
    ///     does not return 413 on H3.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_BeforeRequest_BufferedBodyLimit_Returns413()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "nope")));

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateHttp3Proxy(quicEp, p =>
        {
            p.MaxBufferedBodyBytes = 32;
            p.BeforeRequest += async (_, e) =>
            {
                // Force the buffered-body path that throws BodySizeLimitExceededException.
                _ = await e.GetRequestBody();
            };
        });

        var payload = Encoding.UTF8.GetBytes(new string('x', 256));
        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
        var response = await client.SendAsync(
            "POST", $"localhost:{origin.Port}", "/big", payload);
        Assert.AreEqual(413, response.StatusCode, response.TextBody);
        Assert.AreEqual(0, origin.AcceptedConnectionCount);
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
