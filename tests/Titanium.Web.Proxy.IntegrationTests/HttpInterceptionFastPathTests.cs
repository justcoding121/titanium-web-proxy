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

[TestClass]
[DoNotParallelize]
public class HttpInterceptionFastPathTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H1_Reverse_NoHandlers_DoesNotCallBeforeRequest_AndProxies()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("fast-path-ok"));

        var proxy = testSuite.GetReverseProxy();
        Assert.IsFalse(proxy.NeedsHttpInterception());

        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = new Uri(server.ListeningHttpUrl).Port;

        using var client = testSuite.GetReverseProxyClient();
        var response = await client.GetAsync($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("fast-path-ok", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H1_Reverse_WithBeforeRequest_CallsHandler()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("intercept-ok"));

        var proxy = testSuite.GetReverseProxy();
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = new Uri(server.ListeningHttpUrl).Port;

        var beforeCalled = 0;
        proxy.BeforeRequest += (_, _) =>
        {
            Interlocked.Increment(ref beforeCalled);
            return Task.CompletedTask;
        };

        using var client = testSuite.GetReverseProxyClient();
        var response = await client.GetAsync($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("intercept-ok", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(1, beforeCalled);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H1_Reverse_PredicateFalse_SkipsBeforeRequest_ForThatHost()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("predicate-ok"));

        var proxy = testSuite.GetReverseProxy();
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = new Uri(server.ListeningHttpUrl).Port;

        var beforeCalled = 0;
        proxy.BeforeRequest += (_, _) =>
        {
            Interlocked.Increment(ref beforeCalled);
            return Task.CompletedTask;
        };
        // Gate is on (handler subscribed) but predicate returns false for this reverse host.
        proxy.ShouldInterceptHttp = _ => false;

        using var client = testSuite.GetReverseProxyClient();
        var response = await client.GetAsync($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("predicate-ok", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(0, beforeCalled);
    }
}
