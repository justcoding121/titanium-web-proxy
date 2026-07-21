using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class UpstreamProxyAuthTests
{
    [TestMethod]
    public async Task Authenticates_Https_Connect_To_Upstream_Proxy()
    {
        var testSuite = new TestSuite();
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
        var testSuite = new TestSuite();
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
