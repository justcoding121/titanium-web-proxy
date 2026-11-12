using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>TransparentClientHandler</c> suite: PATCH method on cleartext-forward transparent path.
/// </summary>
[TestClass]
public class TransparentClientHandlerPatchMethodFatGapTests
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
    ///     Bug class: PATCH on transparent cleartext-forward is rejected or mis-routed.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Transparent_Patch_ReachesOrigin()
    {
        string? body = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            if (HttpMethods.IsPatch(ctx.Request.Method))
            {
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                body = await reader.ReadToEndAsync();
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            }
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
            using var content = new StringContent("patch-gap", Encoding.UTF8, "text/plain");
            using var response = await client.PatchAsync("/patch-gap", content);
            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            Assert.AreEqual("patch-gap", body);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
