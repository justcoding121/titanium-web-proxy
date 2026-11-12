using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>TcpConnectionFactory</c> suite: cleartext upstream dials using the <c>127.0.0.1</c> literal.
/// </summary>
[DoNotParallelize]
[TestClass]
public class TcpConnectionFactory127HostFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: upstream pool keys reject numeric loopback host literals for plain HTTP.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Cleartext127Host_ReachesOrigin()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("127-ok"));

        using var client = suite.GetClient(suite.GetProxy());
        var url = $"http://127.0.0.1:{server.HttpListeningPort}/num-loop";
        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        Assert.AreEqual("127-ok", await response.Content.ReadAsStringAsync());
    }
}
