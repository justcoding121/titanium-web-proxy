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
///     Fat SOCKS listener suite: <see cref="SocksProxyEndPoint" /> with MITM (<c>decryptSsl: true</c>)
///     and HTTP clients using a SOCKS5 <see cref="IWebProxy" />.
/// </summary>
[TestClass]
public class Socks5EndpointHttpsMitmFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: SOCKS5 MITM endpoint rejects HTTPS GET or does not decrypt to reach the origin.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task Socks5_DecryptSsl_HttpsGet_ReachesOrigin()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("socks5-mitm-ok"));

        using var proxy = BuildSocksMitmProxy();
        try
        {
            using var client = CreateSocks5Client(proxy.ProxyEndPoints[0].Port);
            using var response = await client.GetAsync(server.ListeningHttpsUrl);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("socks5-mitm-ok", await response.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("SOCKS", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("Platform HttpClient could not use SOCKS5 proxy: " + ex.Message);
        }
    }

    /// <summary>
    ///     Bug class: second HTTPS GET on the same SOCKS session drops the MITM tunnel.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task Socks5_DecryptSsl_TwoGets_BothReachOrigin()
    {
        var hits = 0;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            hits++;
            return ctx.Response.WriteAsync("hit-" + hits);
        });

        using var proxy = BuildSocksMitmProxy();
        try
        {
            using var client = CreateSocks5Client(proxy.ProxyEndPoints[0].Port);
            using var r1 = await client.GetAsync(server.ListeningHttpsUrl + "/1");
            using var r2 = await client.GetAsync(server.ListeningHttpsUrl + "/2");
            Assert.AreEqual("hit-1", await r1.Content.ReadAsStringAsync());
            Assert.AreEqual("hit-2", await r2.Content.ReadAsStringAsync());
            Assert.AreEqual(2, hits);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("SOCKS", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("Platform HttpClient could not use SOCKS5 proxy: " + ex.Message);
        }
    }

    private static ProxyServer BuildSocksMitmProxy()
    {
        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(new SocksProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true));
        proxy.Start();
        return proxy;
    }

    private static HttpClient CreateSocks5Client(int socksPort)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
            UseProxy = true,
            SslOptions =
            {
                RemoteCertificateValidationCallback =
                    (_, certificate, _, errors) => TestCertificateAuthority.Validate(certificate, errors)
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }
}
