using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Characterization for issue #964: transparent endpoint forwarding through an authenticated
///     upstream HTTP proxy (CONNECT with Basic credentials).
/// </summary>
[TestClass]
public class TransparentUpstreamAuthTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Transparent_Https_Through_BasicAuth_Upstream_Succeeds()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("transparent-upstream-ok"));

        var upstream = testSuite.GetProxy();
        // Distinct Via tokens so the intentional proxy chain is not mistaken for a forwarding loop.
        upstream.ViaHeaderPseudonym = "titanium-upstream";
        upstream.ProxyBasicAuthenticateFunc = (_, user, pass) =>
            Task.FromResult(user == "upuser" && pass == "uppass");

        var proxy = testSuite.GetReverseProxy();
        proxy.ViaHeaderPseudonym = "titanium-transparent";
        proxy.UpStreamHttpsProxy = new ExternalProxy("localhost", upstream.ProxyEndPoints[0].Port)
        {
            UserName = "upuser",
            Password = "uppass",
            UseDefaultCredentials = false
        };

        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpsUrl;
            return Task.CompletedTask;
        };

        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions =
            {
                RemoteCertificateValidationCallback =
                    (_, certificate, _, errors) => TestCertificateAuthority.Validate(certificate, errors)
            }
        };
        using var client = new HttpClient(handler);

        // Connect to the transparent proxy as if redirected there (iptables / fixed forward).
        var response = await client.GetAsync($"https://localhost:{proxy.ProxyEndPoints[0].Port}/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("transparent-upstream-ok", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Transparent_Http_Through_BasicAuth_Upstream_Succeeds()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("transparent-http-upstream-ok"));

        var upstream = testSuite.GetProxy();
        // Distinct Via tokens so the intentional proxy chain is not mistaken for a forwarding loop
        // (both would otherwise default to "titanium-web-proxy" and the second hop would return 508).
        upstream.ViaHeaderPseudonym = "titanium-upstream";
        upstream.ProxyBasicAuthenticateFunc = (_, user, pass) =>
            Task.FromResult(user == "upuser" && pass == "uppass");

        var proxy = testSuite.GetReverseProxy();
        proxy.ViaHeaderPseudonym = "titanium-transparent";
        proxy.UpStreamHttpProxy = new ExternalProxy("localhost", upstream.ProxyEndPoints[0].Port)
        {
            UserName = "upuser",
            Password = "uppass",
            UseDefaultCredentials = false
        };
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpUrl;
            return Task.CompletedTask;
        };

        using var client = testSuite.GetReverseProxyClient();
        var response = await client.GetAsync($"http://localhost:{proxy.ProxyEndPoints[0].Port}/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("transparent-http-upstream-ok", await response.Content.ReadAsStringAsync());
    }
}
