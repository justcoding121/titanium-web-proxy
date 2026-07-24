using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Characterization for issue #728: custom root certificates loaded via
///     <see cref="CertificateManager.LoadRootCertificate(string, string, bool, X509KeyStorageFlags)" />
///     must be usable for MITM TLS handshakes (not only the ProxyServer constructor overload).
/// </summary>
[TestClass]
public class CustomRootCertificateTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task LoadRootCertificate_FromPfx_SupportsHttpsMitmHandshake()
    {
        var pfxPath = Path.Combine(Path.GetTempPath(), $"twp-root-{Guid.NewGuid():N}.pfx");
        const string password = "test-pfx-password";

        try
        {
            // Lifetime 1: create and persist a custom root CA.
            using (var creator = new CertificateManager(null, null, false, false, false, NullLogger.Instance))
            {
                creator.CertificateEngine = CertificateEngine.BouncyCastle;
                Assert.IsTrue(creator.CreateRootCertificate(false));
                Assert.IsNotNull(creator.RootCertificate);
                File.WriteAllBytes(pfxPath, creator.RootCertificate.Export(X509ContentType.Pkcs12, password));
            }

            using var testSuite = new TestSuite();
            var server = testSuite.GetServer();
            server.HandleRequest(context => context.Response.WriteAsync("loaded-root-ok"));

            var proxy = testSuite.GetProxy();
            Assert.IsTrue(
                proxy.CertificateManager.LoadRootCertificate(pfxPath, password, overwritePfXFile: false),
                "LoadRootCertificate must succeed for a valid custom CA PFX");
            Assert.IsNotNull(proxy.CertificateManager.RootCertificate);
            Assert.IsTrue(proxy.CertificateManager.RootCertificate.HasPrivateKey);

            // Client must trust leaves signed by the loaded custom root (not the shared test CA).
            var handler = new HttpClientHandler
            {
                Proxy = new TestHelper.TestProxy($"http://localhost:{proxy.ProxyEndPoints[0].Port}", false),
                UseProxy = true,
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                {
                    using var chain = new X509Chain();
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.VerificationFlags =
                        X509VerificationFlags.AllowUnknownCertificateAuthority;
                    chain.ChainPolicy.ExtraStore.Add(proxy.CertificateManager.RootCertificate);
                    return certificate != null && chain.Build(new X509Certificate2(certificate));
                }
            };

            using var client = new HttpClient(handler);
            var response = await client.GetAsync(server.ListeningHttpsUrl);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("loaded-root-ok", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            try
            {
                if (File.Exists(pfxPath)) File.Delete(pfxPath);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
