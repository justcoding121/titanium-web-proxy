#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Dual-listen reverse HTTP/3: TCP H1/H2 + UDP H3 on the same TransparentProxyEndPoint port.
/// </summary>
[TestClass]
[DoNotParallelize]
public class Http3ReverseDualListenTests
{
    private static TestServer sharedServer = null!;

    public TestContext TestContext { get; set; }

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static ProxyServer CreateDualListenProxy(TransparentProxyEndPoint endPoint)
    {
        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp2 = true,
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };

        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return proxy;
    }

    private static SocketsHttpHandler CreateHttpClientHandler(Version version)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions =
            {
                RemoteCertificateValidationCallback = static (_, cert, _, errors) =>
                    TestCertificateAuthority.Validate(cert, errors)
            },
            EnableMultipleHttp2Connections = true,
            EnableMultipleHttp3Connections = true
        };
    }

    [TestMethod]
    public async Task HttpClient_Http3_RoundTrip_Via_DualListen()
    {
        RequireQuic();

        sharedServer.HandleRequest(context => context.Response.WriteAsync("h3-dual-ok"));

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            EnableHttp3 = true,
            ForwardHost = "127.0.0.1",
            ForwardPort = new Uri(sharedServer.ListeningHttpUrl).Port,
            ForwardCleartext = true,
            GenericCertificateName = "localhost",
            MaxInboundBidirectionalStreams = 100
        };
        endPoint.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            return Task.CompletedTask;
        };

        using var proxy = CreateDualListenProxy(endPoint);
        using var handler = CreateHttpClientHandler(HttpVersion.Version30);
        using var client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using var response = await client.GetAsync($"https://localhost:{endPoint.Port}/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual(HttpVersion.Version30, response.Version);
        Assert.AreEqual("h3-dual-ok", body);
    }

    /// <summary>
    ///     RPS twin of <c>twp-reverse-http3-to-https-http1</c>: client H3, ForwardHost=127.0.0.1,
    ///     origin HTTPS HTTP/1. Outbound TLS uses <c>SupportedSslProtocols</c> (not inbound QUIC
    ///     Tls13), so macOS SecureTransport can negotiate TLS 1.2.
    /// </summary>
    [TestMethod]
    public async Task HttpClient_Http3_To_HttpsHttp1_ForwardHostIp()
    {
        RequireQuic();

        sharedServer.HandleRequest(context => context.Response.WriteAsync("h3-to-https-h1"));

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            EnableHttp3 = true,
            ForwardHost = "127.0.0.1",
            ForwardPort = sharedServer.HttpsListeningPort,
            ForwardCleartext = false,
            GenericCertificateName = "localhost",
            MaxInboundBidirectionalStreams = 100
        };
        endPoint.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var proxy = CreateDualListenProxy(endPoint);
        using var handler = CreateHttpClientHandler(HttpVersion.Version30);
        using var client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using var response = await client.GetAsync($"https://localhost:{endPoint.Port}/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual(HttpVersion.Version30, response.Version);
        Assert.AreEqual("h3-to-https-h1", body);
    }

    [TestMethod]
    public async Task HttpClient_Http2_SamePort_Injects_AltSvc()
    {
        RequireQuic();

        sharedServer.HandleRequest(context => context.Response.WriteAsync("h2-altsvc"));

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            EnableHttp3 = true,
            ForwardHost = "127.0.0.1",
            ForwardPort = new Uri(sharedServer.ListeningHttpUrl).Port,
            ForwardCleartext = true,
            GenericCertificateName = "localhost"
        };
        endPoint.BeforeSslAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        endPoint.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            return Task.CompletedTask;
        };

        using var proxy = CreateDualListenProxy(endPoint);
        using var handler = CreateHttpClientHandler(HttpVersion.Version20);
        using var client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using var response = await client.GetAsync($"https://localhost:{endPoint.Port}/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("h2-altsvc", body);

        Assert.IsTrue(response.Headers.TryGetValues("Alt-Svc", out var values));
        var altSvc = values.Single();
        StringAssert.Contains(altSvc, $"h3=\":{endPoint.Port}\"");
    }
}
