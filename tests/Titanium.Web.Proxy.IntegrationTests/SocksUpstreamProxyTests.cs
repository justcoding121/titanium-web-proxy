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
///     Characterization for issue #875: HTTP and HTTPS traffic through a SOCKS5 upstream proxy.
/// </summary>
[TestClass]
public class SocksUpstreamProxyTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Http_Through_Socks5_Upstream_Succeeds()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("socks5-http-ok"));

        using var socksUpstream = BuildSocksUpstream();
        var proxy = testSuite.GetProxy();
        proxy.UpStreamHttpProxy = new ExternalProxy
        {
            HostName = "127.0.0.1",
            Port = socksUpstream.ProxyEndPoints[0].Port,
            ProxyType = ExternalProxyType.Socks5
        };

        using var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(server.ListeningHttpUrl);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("socks5-http-ok", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Https_Through_Socks5_Upstream_Succeeds()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("socks5-https-ok"));

        using var socksUpstream = BuildSocksUpstream();
        var proxy = testSuite.GetProxy();
        proxy.UpStreamHttpsProxy = new ExternalProxy
        {
            HostName = "127.0.0.1",
            Port = socksUpstream.ProxyEndPoints[0].Port,
            ProxyType = ExternalProxyType.Socks5
        };

        using var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(server.ListeningHttpsUrl);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("socks5-https-ok", await response.Content.ReadAsStringAsync());
    }

    private static ProxyServer BuildSocksUpstream()
    {
        var proxyServer = new ProxyServer(false, false, false);
        proxyServer.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxyServer.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxyServer.AddEndPoint(new SocksProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        proxyServer.Start();
        return proxyServer;
    }
}
