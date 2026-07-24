using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class UpstreamProxyChainTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task TwoHop_Http_Upstream_Chain_Reaches_Https_Origin()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("chained-ok"));

        // Intermediate hops must not decrypt — they only forward CONNECT tunnels.
        using var hop2 = CreateTunnelOnlyProxy();
        using var hop1 = CreateTunnelOnlyProxy();

        var proxy = testSuite.GetProxy();
        proxy.UpStreamHttpsProxy = new ExternalProxy("localhost", hop1.ProxyEndPoints[0].Port)
        {
            NextHop = new ExternalProxy("localhost", hop2.ProxyEndPoints[0].Port)
        };

        using var client = testSuite.GetClient(proxy);
        var body = await client.GetStringAsync(server.ListeningHttpsUrl);
        Assert.AreEqual("chained-ok", body);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task TwoHop_Http_Upstream_Chain_With_Basic_Auth_On_Each_Hop()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("auth-chain-ok"));

        using var hop2 = CreateTunnelOnlyProxy();
        hop2.ProxyBasicAuthenticateFunc = (_, user, pass) =>
            Task.FromResult(user == "hop2" && pass == "p2");

        using var hop1 = CreateTunnelOnlyProxy();
        hop1.ProxyBasicAuthenticateFunc = (_, user, pass) =>
            Task.FromResult(user == "hop1" && pass == "p1");

        var proxy = testSuite.GetProxy();
        proxy.UpStreamHttpsProxy = new ExternalProxy("localhost", hop1.ProxyEndPoints[0].Port, "hop1", "p1")
        {
            NextHop = new ExternalProxy("localhost", hop2.ProxyEndPoints[0].Port, "hop2", "p2")
        };

        using var client = testSuite.GetClient(proxy);
        var body = await client.GetStringAsync(server.ListeningHttpsUrl);
        Assert.AreEqual("auth-chain-ok", body);
    }

    [TestMethod]
    public void CacheKey_Includes_NextHop()
    {
        var factory = new Network.Tcp.TcpConnectionFactory(new ProxyServer());
        try
        {
            var single = new ExternalProxy("proxy1.example", 8080, "u", "p");
            var chained = new ExternalProxy("proxy1.example", 8080, "u", "p")
            {
                NextHop = new ExternalProxy("proxy2.example", 8080, "u2", "p2")
            };

            var key1 = factory.GetConnectionCacheKey("example.com", 443, true, null, null, single);
            var key2 = factory.GetConnectionCacheKey("example.com", 443, true, null, null, chained);

            Assert.AreNotEqual(key1, key2);
        }
        finally
        {
            factory.Dispose();
        }
    }

    private static ProxyServer CreateTunnelOnlyProxy()
    {
        var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        endPoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.DecryptSsl = false;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return proxy;
    }
}
