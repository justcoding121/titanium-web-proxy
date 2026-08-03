using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Guarantees reverse/transparent HTTPS tests keep working when a developer machine has been used
///     with the Basic/WPF examples:
///     <list type="bullet">
///         <item>
///             The product default root (<c>Titanium Root Certificate Authority</c>) may be trusted in the
///             current-user Windows stores (<c>new ProxyServer()</c> trusts on <c>Start()</c>).
///         </item>
///         <item>
///             WinINET/system proxy may point at a concurrently running example (e.g. localhost:8000).
///             Direct test clients must use <c>UseProxy = false</c> (see <c>TestHelper.GetHttpClient()</c>).
///         </item>
///     </list>
/// </summary>
[DoNotParallelize]
[TestClass]
public class CertificateStoreIsolationTests
{
    private static TestServer sharedServer = null!;

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

    [TestMethod]
    public void TestRoot_Subject_Differs_From_ProductDefault()
    {
        Assert.AreEqual("Titanium Integration Test Root CA", TestCertificateAuthority.RootCertificateName);
        Assert.AreEqual(
            "CN=Titanium Integration Test Root CA",
            TestCertificateAuthority.RootCertificate.Subject);
        Assert.AreNotEqual(
            "CN=Titanium Root Certificate Authority",
            TestCertificateAuthority.RootCertificate.Subject,
            "Test CA must not share the product default subject DN used by Basic/WPF examples.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Https_ReverseProxy_Succeeds_Independently_Of_System_Proxy_And_Product_Root()
    {
        // Product root may be in CurrentUser\Root (from the Basic example), and WinINET may point
        // at a live example on :8000. GetReverseProxyClient must still reach the in-process reverse
        // proxy and accept its test-CA-signed leaf.
        Console.WriteLine(
            $"Product default root in user store: {UserStoreContainsProductDefaultRoot()}");
        Console.WriteLine(
            $"DefaultProxy for localhost: {HttpClient.DefaultProxy.GetProxy(new Uri("https://localhost:9/"))}");

        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("coexistence-ok"));

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpsUrl;
            return Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();
        var response = await client.PostAsync(
            new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}/"),
            new StringContent("hello"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("coexistence-ok", await response.Content.ReadAsStringAsync());
    }

    private static bool UserStoreContainsProductDefaultRoot()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Cast<X509Certificate2>().Any(c =>
            string.Equals(c.Subject, "CN=Titanium Root Certificate Authority", StringComparison.Ordinal));
    }
}
