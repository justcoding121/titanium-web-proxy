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
///     Fat <c>TransparentClientHandler</c> suite: TLS client to cleartext origin forward and keep-alive reuse.
/// </summary>
[DoNotParallelize]
[TestClass]
public class TransparentClientHandlerCleartextForwardFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static HttpClient TransparentClient(TransparentProxyEndPoint ep) =>
        new(new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null,
        })
        {
            BaseAddress = new Uri($"https://127.0.0.1:{ep.Port}"),
            Timeout = TimeSpan.FromSeconds(20),
        };

    /// <summary>
    ///     Bug class: transparent decrypt-to-cleartext forward drops the second keep-alive request.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Transparent_CleartextForward_KeepAlive_TwoGets()
    {
        var hits = 0;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            hits++;
            await ctx.Response.WriteAsync("hit-" + hits);
        });

        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false, EnableHttpInterception = true };
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
            ForwardHost = "127.0.0.1",
            ForwardPort = server.HttpListeningPort,
            ForwardCleartext = true,
        };
        proxy.AddEndPoint(ep);
        proxy.Start();

        try
        {
            using var client = TransparentClient(ep);
            using var first = await client.GetAsync("/a");
            using var second = await client.GetAsync("/b");
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
            Assert.AreEqual("hit-1", await first.Content.ReadAsStringAsync());
            Assert.AreEqual("hit-2", await second.Content.ReadAsStringAsync());
            Assert.AreEqual(2, hits);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
