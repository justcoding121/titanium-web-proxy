using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>TcpConnectionFactory</c> suite: HTTPS to a loopback port that refuses SYN becomes a
///     synthetic 502/5xx at the explicit proxy (no hang).
/// </summary>
[DoNotParallelize]
[TestClass]
public class TcpConnectionFactoryRefusedOriginFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: connection refused to the origin hangs the client instead of returning 502.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Https_RefusedOrigin_Returns5xx()
    {
        var refusedPort = AllocateRefusedLoopbackPort();
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);

        using var response = await client.GetAsync($"https://127.0.0.1:{refusedPort}/");
        Assert.IsTrue((int)response.StatusCode is >= 500 and < 600,
            $"Refused origin must surface as 5xx; got {(int)response.StatusCode}");
    }

    /// <summary>
    ///     Bug class: plain HTTP to a refused port on the explicit proxy wedges RequestHandler.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Http_RefusedOrigin_Returns5xx()
    {
        var refusedPort = AllocateRefusedLoopbackPort();
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);

        using var response = await client.GetAsync($"http://127.0.0.1:{refusedPort}/");
        Assert.IsTrue((int)response.StatusCode is >= 500 and < 600,
            $"Refused HTTP origin must surface as 5xx; got {(int)response.StatusCode}");
    }

    private static int AllocateRefusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
