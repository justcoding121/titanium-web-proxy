using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <see cref="ProxyServer.GetCustomUpStreamProxyFunc" /> suite: per-request upstream selection
///     distinct from <c>SessionEventArgs.CustomUpStreamProxy</c> mutation in BeforeRequest.
/// </summary>
[DoNotParallelize]
[TestClass]
public class CustomUpstreamProxyFuncFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: GetCustomUpStreamProxyFunc return value is ignored for HTTPS CONNECT nesting.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task GetCustomUpStreamProxyFunc_SelectedPath_UsesNestedUpstream()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            var marker = ctx.Request.Path.Value?.Contains("via-func", StringComparison.Ordinal) == true
                ? "via-func"
                : "direct";
            return ctx.Response.WriteAsync(marker);
        });

        using var upstreamSuite = new TestSuite(sharedServer);
        var upstream = upstreamSuite.GetProxy();
        var upstreamConnects = 0;
        var upstreamEp = upstream.ProxyEndPoints.OfType<ExplicitProxyEndPoint>().First();
        upstreamEp.BeforeTunnelConnectRequest += (_, _) =>
        {
            Interlocked.Increment(ref upstreamConnects);
            return Task.CompletedTask;
        };

        var proxy = suite.GetProxy();
        proxy.GetCustomUpStreamProxyFunc = session =>
        {
            var path = session.HttpClient.Request.RequestUri.AbsolutePath;
            if (path.Contains("via-func", StringComparison.Ordinal))
                return Task.FromResult<IExternalProxy?>(
                    new ExternalProxy("127.0.0.1", upstream.ProxyEndPoints[0].Port));
            return Task.FromResult<IExternalProxy?>(null);
        };

        using var client = suite.GetClient(proxy);
        var via = await client.GetStringAsync(server.ListeningHttpsUrl.TrimEnd('/') + "/via-func");
        var direct = await client.GetStringAsync(server.ListeningHttpsUrl.TrimEnd('/') + "/direct-gap");
        Assert.AreEqual("via-func", via);
        Assert.AreEqual("direct", direct);
        Assert.IsTrue(upstreamConnects >= 1);
    }

    /// <summary>
    ///     Bug class: clearing GetCustomUpStreamProxyFunc while running leaves a stale delegate wired.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task GetCustomUpStreamProxyFunc_NullAfterStart_GoesDirect()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("direct-only"));

        var proxy = suite.GetProxy();
        var nestedCalls = 0;
        proxy.GetCustomUpStreamProxyFunc = _ =>
        {
            Interlocked.Increment(ref nestedCalls);
            return Task.FromResult<IExternalProxy?>(new ExternalProxy("127.0.0.1", 9));
        };
        proxy.GetCustomUpStreamProxyFunc = null;

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/d");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(0, nestedCalls);
        Assert.AreEqual("direct-only", await response.Content.ReadAsStringAsync());
    }
}
