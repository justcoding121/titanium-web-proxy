using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.Models;

using Titanium.Web.Proxy.IntegrationTests.Setup;
namespace Titanium.Web.Proxy.IntegrationTests;

[DoNotParallelize]
[TestClass]
public class UpstreamProxyAuthTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    [TestMethod]
    public async Task Authenticates_Https_Connect_To_Upstream_Proxy()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("secure target response"));

        using var upstreamProxy = new FakeUpstreamProxy(server.HttpsListeningPort);
        using var proxy = CreateProxy(testSuite, upstreamProxy, useForHttps: true);
        using var client = testSuite.GetClient(proxy);

        var body = await client.GetStringAsync(server.ListeningHttpsUrl);

        Assert.AreEqual("secure target response", body);
        CollectionAssert.AreEqual(new[] { string.Empty, "NTLM t1", "NTLM t2" },
            upstreamProxy.ProxyAuthorizationValues.ToArray());
    }

    [TestMethod]
    public async Task Authenticates_Plain_Http_Request_To_Upstream_Proxy()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        using var upstreamProxy = new FakeUpstreamProxy(server.HttpsListeningPort);
        using var proxy = CreateProxy(testSuite, upstreamProxy, useForHttps: false);
        using var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(server.ListeningHttpUrl);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode,
            string.Join(", ", upstreamProxy.ProxyAuthorizationValues));
        Assert.AreEqual("authenticated plain HTTP", body);
        CollectionAssert.AreEqual(new[] { string.Empty, "NTLM t1", "NTLM t2" },
            upstreamProxy.ProxyAuthorizationValues.ToArray());
    }

    /// <summary>
    /// Regression for issue #857: a non-success upstream CONNECT response must surface as
    /// <see cref="UpstreamProxyConnectException"/> carrying status, headers, and a body preview
    /// instead of a generic "failed to create a secure tunnel" exception that discards them.
    /// </summary>
    [TestMethod]
    public async Task Rejected_Upstream_Connect_Surfaces_Typed_Status_Headers_And_Body()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("should not reach"));

        using var upstreamProxy = new RejectingUpstreamProxy(403, "Forbidden", "access denied by upstream");
        var proxy = testSuite.GetProxy();
        proxy.UpStreamHttpsProxy = new ExternalProxy("localhost", upstreamProxy.Port)
        {
            UseDefaultCredentials = false
        };

        var capture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = capture;
        proxy.ApplyLoggingConfiguration();

        using var client = testSuite.GetClient(proxy);

        try
        {
            await client.GetStringAsync(server.ListeningHttpsUrl);
            Assert.Fail("Expected the HTTPS request through the rejecting upstream proxy to fail.");
        }
        catch (Exception)
        {
            // Client-side failure is expected; assert the typed diagnostic below.
        }

        UpstreamProxyConnectException? typed = null;
        for (var i = 0; i < 50 && typed == null; i++)
        {
            typed = capture.Exceptions.OfType<UpstreamProxyConnectException>().FirstOrDefault()
                    ?? UnwrapUpstream(capture.LastException);
            if (typed == null) await Task.Delay(50);
        }

        Assert.IsNotNull(typed, "Expected UpstreamProxyConnectException in proxy diagnostics.");
        Assert.AreEqual(403, typed.StatusCode);
        Assert.AreEqual("Forbidden", typed.StatusDescription);
        Assert.IsTrue(typed.Headers.ContainsKey("Proxy-Authenticate"),
            "Upstream Proxy-Authenticate header must be preserved.");
        Assert.AreEqual("access denied by upstream", typed.BodyPreview);
    }

    private static UpstreamProxyConnectException? UnwrapUpstream(Exception? exception)
    {
        while (exception != null)
        {
            if (exception is UpstreamProxyConnectException typed) return typed;
            exception = exception.InnerException;
        }

        return null;
    }

    private static ProxyServer CreateProxy(TestSuite testSuite, FakeUpstreamProxy upstreamProxy, bool useForHttps)
    {
        var proxy = testSuite.GetProxy();
        var externalProxy = new ExternalProxy("localhost", upstreamProxy.Port)
        {
            UseDefaultCredentials = true
        };

        if (useForHttps)
            proxy.UpStreamHttpsProxy = externalProxy;
        else
            proxy.UpStreamHttpProxy = externalProxy;

        // EnableWinAuth must not corrupt the upstream proxy authentication state on a 407.
        proxy.EnableWinAuth = true;

        proxy.UpstreamProxyWinAuthTokenGenerator = (_, _, challenge, _) =>
            challenge == null ? " t1" : " t2";
        return proxy;
    }
}
