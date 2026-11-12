using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>TcpConnectionFactory</c> suite: HTTPS via cleartext upstream proxy and back-to-back
///     CONNECT-authenticated exchanges.
/// </summary>
[DoNotParallelize]
[TestClass]
public class TcpConnectionFactoryPlainUpstreamFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: the second HTTPS GET through <see cref="ProxyServer.UpStreamHttpProxy" />
    ///     loses NTLM state and never completes CONNECT.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task PlainUpstreamHttp_HttpsConnect_TwoGets_BothReachOrigin()
    {
        var hits = 0;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            hits++;
            return ctx.Response.WriteAsync("https-upstream-" + hits);
        });

        using var upstreamProxy = new FakeUpstreamProxy(server.HttpsListeningPort);
        using var proxy = CreatePlainUpstreamProxy(suite, upstreamProxy);
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);

        var url = server.ListeningHttpsUrl;
        using var r1 = await client.GetAsync(url).WaitAsync(System.TimeSpan.FromSeconds(25));
        using var r2 = await client.GetAsync(url + "/two").WaitAsync(System.TimeSpan.FromSeconds(25));
        r1.EnsureSuccessStatusCode();
        r2.EnsureSuccessStatusCode();
        Assert.AreEqual("https-upstream-1", await r1.Content.ReadAsStringAsync());
        Assert.AreEqual("https-upstream-2", await r2.Content.ReadAsStringAsync());
        Assert.AreEqual(2, hits);
    }

    private static ProxyServer CreatePlainUpstreamProxy(TestSuite suite, FakeUpstreamProxy upstreamProxy)
    {
        var proxy = suite.GetProxy();
        proxy.UpStreamHttpProxy = new ExternalProxy("127.0.0.1", upstreamProxy.Port)
        {
            UseDefaultCredentials = true,
        };
        proxy.EnableWinAuth = true;
        proxy.UpstreamProxyWinAuthTokenGenerator = (_, _, challenge, _) =>
            challenge == null ? " t1" : " t2";
        return proxy;
    }
}
