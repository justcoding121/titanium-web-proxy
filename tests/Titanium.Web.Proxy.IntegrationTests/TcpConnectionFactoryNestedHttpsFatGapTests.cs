using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>TcpConnectionFactory</c> suite: nested HTTPS upstream proxy CONNECT with NTLM
///     must complete for back-to-back client requests.
/// </summary>
[DoNotParallelize]
[TestClass]
public class TcpConnectionFactoryNestedHttpsFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: second GET through the same nested upstream proxy loses NTLM state.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task NestedUpstreamHttps_TwoGets_BothReachOrigin()
    {
        var hits = 0;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            hits++;
            return ctx.Response.WriteAsync("nested-" + hits);
        });

        using var upstreamProxy = new FakeUpstreamProxy(server.HttpsListeningPort);
        using var proxy = CreateNestedProxy(suite, upstreamProxy);

        using var client = suite.GetClient(proxy);
        var first = await client.GetStringAsync(server.ListeningHttpsUrl);
        var second = await client.GetStringAsync(server.ListeningHttpsUrl);

        Assert.AreEqual("nested-1", first);
        Assert.AreEqual("nested-2", second);
        Assert.AreEqual(2, hits);
        Assert.IsFalse(upstreamProxy.ProxyAuthorizationValues.IsEmpty,
            "Nested CONNECT must authenticate at least once.");
    }

    /// <summary>
    ///     Bug class: parallel nested CONNECT attempts wedge the upstream NTLM handshake.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task NestedUpstreamHttps_ParallelGets_AllSucceed()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync(ctx.Request.Path));

        using var upstreamProxy = new FakeUpstreamProxy(server.HttpsListeningPort);
        using var proxy = CreateNestedProxy(suite, upstreamProxy);

        using var client = suite.GetClient(proxy);
        var url = server.ListeningHttpsUrl;
        var tasks = Enumerable.Range(0, 3)
            .Select(_ => client.GetAsync(url))
            .ToArray();
        var responses = await Task.WhenAll(tasks);
        foreach (var response in responses)
        {
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            Assert.IsFalse(string.IsNullOrEmpty(body));
        }
    }

    private static ProxyServer CreateNestedProxy(TestSuite suite, FakeUpstreamProxy upstreamProxy)
    {
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.UpStreamHttpsProxy = new ExternalProxy("127.0.0.1", upstreamProxy.Port)
        {
            UseDefaultCredentials = true,
        };
        proxy.EnableWinAuth = true;
        proxy.UpstreamProxyWinAuthTokenGenerator = (_, _, challenge, _) =>
            challenge == null ? " t1" : " t2";
        return proxy;
    }
}
