using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>TcpConnectionFactory</c> suite: back-to-back HTTPS GETs reuse the upstream pool safely.
/// </summary>
[DoNotParallelize]
[TestClass]
public class TcpConnectionFactoryBackToBackHttpsFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: the second pooled HTTPS GET stalls after the first response.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task BackToBackHttpsGet_BothSucceed_WithPool()
    {
        var count = 0;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            count++;
            return ctx.Response.WriteAsync("n=" + count);
        });

        var proxy = suite.GetProxy();
        proxy.EnableConnectionPool = true;
        using var client = suite.GetClient(proxy);
        var url = server.ListeningHttpsUrl + "/seq";

        var first = await client.GetStringAsync(url);
        var second = await client.GetStringAsync(url);
        Assert.AreEqual("n=1", first);
        Assert.AreEqual("n=2", second);
    }
}
