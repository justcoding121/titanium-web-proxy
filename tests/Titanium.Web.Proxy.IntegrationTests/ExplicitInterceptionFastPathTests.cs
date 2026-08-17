using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
/// Explicit-endpoint DecryptSsl=true uses the same HTTP fast-forward gate as reverse (Phases A/B).
/// DecryptSsl=false remains opaque SendRaw and is not covered here.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ExplicitInterceptionFastPathTests
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
    public async Task Explicit_DecryptSsl_PredicateFalse_SkipsBeforeRequest_AndForwards()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("explicit-passthrough"));

        var proxy = testSuite.GetProxy();
        Assert.IsTrue(proxy.ProxyEndPoints[0].DecryptSsl);

        var beforeCalled = 0;
        proxy.BeforeRequest += (_, _) =>
        {
            Interlocked.Increment(ref beforeCalled);
            return Task.CompletedTask;
        };
        proxy.ShouldInterceptHttp = _ => false;

        using var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(server.ListeningHttpsUrl);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("explicit-passthrough", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(0, beforeCalled);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Explicit_DecryptSsl_PredicateTrue_CallsBeforeRequest_AndAllowsMutation()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        string? seenHeader = null;
        server.HandleRequest(context =>
        {
            seenHeader = context.Request.Headers["X-Intercepted"];
            return context.Response.WriteAsync("explicit-intercept");
        });

        var proxy = testSuite.GetProxy();
        var beforeCalled = 0;
        proxy.BeforeRequest += (_, e) =>
        {
            Interlocked.Increment(ref beforeCalled);
            e.HttpClient.Request.Headers.AddHeader("X-Intercepted", "yes");
            return Task.CompletedTask;
        };
        proxy.ShouldInterceptHttp = _ => true;

        using var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(server.ListeningHttpsUrl);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("explicit-intercept", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(1, beforeCalled);
        Assert.AreEqual("yes", seenHeader);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Explicit_DecryptSsl_NoHandlers_ProxiesWithoutInterceptionGate()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("explicit-no-handlers"));

        var proxy = testSuite.GetProxy();
        Assert.IsFalse(proxy.NeedsHttpInterception());

        using var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(server.ListeningHttpsUrl);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("explicit-no-handlers", await response.Content.ReadAsStringAsync());
    }
}
