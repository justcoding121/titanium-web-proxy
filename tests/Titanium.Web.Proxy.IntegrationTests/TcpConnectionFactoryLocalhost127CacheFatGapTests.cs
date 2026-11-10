using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>TcpConnectionFactory</c> suite: <c>localhost</c> and <c>127.0.0.1</c> literals
///     must not collide in the upstream pool.
/// </summary>
[DoNotParallelize]
[TestClass]
public class TcpConnectionFactoryLocalhost127CacheFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: numeric loopback and localhost share one stale pool entry.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task LocalhostAnd127_BothReachOrigin()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            var host = ctx.Request.Host.Host;
            return ctx.Response.WriteAsync("host=" + host);
        });

        var proxy = suite.GetProxy();
        proxy.EnableConnectionPool = true;
        using var client = suite.GetClient(proxy);
        var port = server.HttpListeningPort;

        var viaName = await client.GetStringAsync($"http://localhost:{port}/a");
        var viaNum = await client.GetStringAsync($"http://127.0.0.1:{port}/b");
        StringAssert.Contains(viaName, "localhost");
        StringAssert.Contains(viaNum, "127.0.0.1");
    }
}
