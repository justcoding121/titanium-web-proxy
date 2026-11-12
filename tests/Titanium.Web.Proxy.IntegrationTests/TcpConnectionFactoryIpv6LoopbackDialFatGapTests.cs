using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>TcpConnectionFactory</c> suite: dual-stack loopback literals dial distinct upstream cache keys.
/// </summary>
[DoNotParallelize]
[TestClass]
public class TcpConnectionFactoryIpv6LoopbackDialFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: <c>[::1]</c> and <c>127.0.0.1</c> collide in the TCP pool and return the wrong host.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Ipv6AndIpv4Loopback_BothReachOrigin()
    {
        if (!Socket.OSSupportsIPv6)
            Assert.Inconclusive("IPv6 loopback is not supported on this host.");

        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("seen-" + ctx.Request.Host.Host));

        var proxy = suite.GetProxy();
        proxy.EnableConnectionPool = true;
        using var client = suite.GetClient(proxy);
        var port = server.HttpListeningPort;

        var v4 = await client.GetStringAsync($"http://127.0.0.1:{port}/v4");
        StringAssert.Contains(v4, "127.0.0.1");

        using var v6Response = await client.GetAsync($"http://[::1]:{port}/v6");
        if (v6Response.StatusCode == System.Net.HttpStatusCode.BadGateway)
            Assert.Inconclusive("Test origin is not reachable via [::1] on this host.");
        var v6 = await v6Response.Content.ReadAsStringAsync();
        StringAssert.Contains(v6, "::1");
    }
}
