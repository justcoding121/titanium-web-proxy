using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: ProxyBasicAuthenticate reject → 407 with
///     OnBeforeResponse (absolute-form HTTP), and accept with credentials that
///     continue to the origin (HTTPS CONNECT).
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerAuth407FatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static HttpClient AbsoluteFormClient(ProxyServer proxy)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}"),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    ///     Bug class: rejected ProxyBasicAuthenticateFunc returns nothing (or hangs)
    ///     instead of a 407 that runs OnBeforeResponse on the absolute-form path.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BasicAuthReject_Returns407_AndFiresBeforeResponse()
    {
        var beforeResponse = 0;
        var originHits = 0;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(_ =>
        {
            System.Threading.Interlocked.Increment(ref originHits);
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.ProxyBasicAuthenticateFunc = (_, _, _) => Task.FromResult(false);
        proxy.BeforeResponse += (_, _) =>
        {
            System.Threading.Interlocked.Increment(ref beforeResponse);
            return Task.CompletedTask;
        };

        using var client = AbsoluteFormClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpUrl + "/denied");
        Assert.AreEqual(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
        Assert.AreEqual(0, originHits, "Rejected auth must not contact the origin.");
        for (var i = 0; i < 40 && System.Threading.Volatile.Read(ref beforeResponse) == 0; i++)
            await Task.Delay(50);
        Assert.AreEqual(1, System.Threading.Volatile.Read(ref beforeResponse),
            "OnBeforeResponse must run for the synthetic 407.");
    }

    /// <summary>
    ///     Bug class: accepted credentials still fail CONNECT / never reach the origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BasicAuthAccept_ReachesOrigin()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("auth-ok"));

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.ProxyBasicAuthenticateFunc = (_, user, pass) =>
            Task.FromResult(user == "test" && pass == "Test56");

        using var client = suite.GetClient(proxy, enableBasicProxyAuthorization: true);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/ok");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("auth-ok", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    ///     Bug class: absolute-form HTTP with rejected auth does not answer 407 on cleartext.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BasicAuthReject_AbsoluteFormHttp_Returns407()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(_ => Task.CompletedTask);

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.ProxyBasicAuthenticateFunc = (_, _, _) => Task.FromResult(false);

        using var client = AbsoluteFormClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpUrl + "/abs-denied");
        Assert.AreEqual(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
    }

    /// <summary>
    ///     Bug class: wrong Proxy-Authorization credentials are treated as authenticated.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BasicAuthWrongPassword_Returns407()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("nope"));

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.ProxyBasicAuthenticateFunc = (_, user, pass) =>
            Task.FromResult(user == "test" && pass == "Test56");

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}")
            {
                Credentials = new NetworkCredential("test", "wrong"),
            },
            UseProxy = true,
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(server.ListeningHttpUrl + "/wrong");
        Assert.AreEqual(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
    }
}
