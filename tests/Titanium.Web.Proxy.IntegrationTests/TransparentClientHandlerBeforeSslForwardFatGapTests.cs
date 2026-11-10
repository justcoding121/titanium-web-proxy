using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>TransparentClientHandler</c> suite: <c>BeforeSslAuthenticate</c> on the ClientHello
///     path and HTTPS-to-HTTPS reverse terminate forwarding.
/// </summary>
[DoNotParallelize]
[TestClass]
public class TransparentClientHandlerBeforeSslForwardFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static HttpClient TransparentClient(int port) =>
        new(new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null,
        })
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(20),
        };

    /// <summary>
    ///     Bug class: transparent peek path ignores <c>BeforeSslAuthenticate</c> upstream protocol
    ///     and cannot reach a cleartext origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Transparent_BeforeSsl_H1Upstream_ReachesCleartextOrigin()
    {
        const string body = "before-ssl-h1";
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync(body));

        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = true, EnableHttpInterception = true };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };

        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            GenericCertificateName = "localhost",
            ForwardHost = "127.0.0.1",
            ForwardPort = server.HttpListeningPort,
            ForwardCleartext = true,
        };
        ep.BeforeSslAuthenticate += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(ep);
        proxy.Start();

        try
        {
            using var client = TransparentClient(ep.Port);
            using var response = await client.GetAsync("/before-ssl")
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(body, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    /// <summary>
    ///     Bug class: transparent terminate-to-TLS origin (ForwardCleartext=false) answers BadGateway.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Transparent_HttpsReencrypt_Get_ReachesTlsOrigin()
    {
        using var origin = new Helpers.Http11OnlyOriginServer(TestCertificateAuthority.ServerCertificate);
        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };

        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
            ForwardCleartext = false,
        };
        proxy.AddEndPoint(ep);
        proxy.Start();

        try
        {
            using var client = TransparentClient(ep.Port);
            using var response = await client.GetAsync("/tls-gap")
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("h1-only-origin-ok", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
