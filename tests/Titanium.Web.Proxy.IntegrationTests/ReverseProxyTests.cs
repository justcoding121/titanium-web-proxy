using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Tests share a single Kestrel <see cref="TestServer" /> instance (started once for the class)
///     to avoid paying the ~200–400 ms host-start cost on every test. Tests within this class are
///     serialised (<see cref="DoNotParallelizeAttribute" />) so that each test's
///     <see cref="TestServer.HandleRequest" /> assignment is not racy; they still run concurrently
///     with tests in other classes.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ReverseProxyTests
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
    public async Task Smoke_Test_Http_To_Http_Reverse_Proxy()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpUrl;
            await Task.FromResult(0);
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Https_To_Http_Reverse_Proxy()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpUrl;
            await Task.FromResult(0);
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Http_To_Https_Reverse_Proxy()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpsUrl;
            await Task.FromResult(0);
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Https_To_Https_Reverse_Proxy()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpsUrl;
            await Task.FromResult(0);
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Https_To_Https_Reverse_Proxy_Tunnel_Without_Decryption()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        var endpoint =
            (TransparentProxyEndPoint)proxy.ProxyEndPoints.Where(x => x is TransparentProxyEndPoint).First();

        endpoint.BeforeSslAuthenticate += async (sender, e) =>
        {
            e.DecryptSsl = false;
            e.ForwardHttpsPort = server.HttpsListeningPort;
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Http_Reverse_Proxy_With_Fixed_Forward_Endpoint()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();

        // forward everything to the fixed backend without rewriting the request in BeforeRequest.
        endpoint.ForwardHost = "localhost";
        endpoint.ForwardPort = server.HttpListeningPort;

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Https_Reverse_Proxy_With_Fixed_Forward_Endpoint()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();

        // decrypt and forward to the fixed backend; the original "localhost" host is still
        // used for TLS SNI/certificate validation while only the connection port changes.
        endpoint.ForwardHost = "localhost";
        endpoint.ForwardPort = server.HttpsListeningPort;

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Https_Reverse_Proxy_Tunnel_With_Fixed_Forward_Endpoint()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();

        // configure the fixed forward on the endpoint; the tunnel path should pick it up
        // as the default forward target without a BeforeSslAuthenticate handler.
        endpoint.ForwardPort = server.HttpsListeningPort;
        endpoint.BeforeSslAuthenticate += async (sender, e) =>
        {
            e.DecryptSsl = false;
            await Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }
}
