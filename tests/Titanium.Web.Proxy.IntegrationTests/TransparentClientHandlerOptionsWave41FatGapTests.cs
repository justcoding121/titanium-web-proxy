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
///     Fat <c>TransparentClientHandler</c> suite: OPTIONS on cleartext-forward transparent path.
/// </summary>
[TestClass]
public class TransparentClientHandlerOptionsWave41FatGapTests
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

    [TestMethod]
    [Timeout(30_000)]
    public async Task Transparent_Options_ReachesOrigin()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                ctx.Response.Headers.Allow = "GET,OPTIONS";
            }

            return Task.CompletedTask;
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
            using var client = TransparentClient(ep.Port);
            using var request = new HttpRequestMessage(HttpMethod.Options, "/opts-gap");
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode,
                await response.Content.ReadAsStringAsync());
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
