using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat ResponseHandler edges: BeforeResponse rewrite/status, AfterResponse,
///     empty body short-circuit, multi-header mutation, and HEAD with Content-Length.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ResponseHandlerInterimAndEncodingFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeResponse_SetsCustomStatusAndHeader()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("orig"));
        var proxy = suite.GetProxy();
        proxy.BeforeResponse += (_, e) =>
        {
            e.HttpClient.Response.StatusCode = (int)HttpStatusCode.Accepted;
            e.HttpClient.Response.Headers.AddHeader("X-Gap", "1");
            e.SetResponseBodyString("custom");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/custom");
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.IsTrue(response.Headers.TryGetValues("X-Gap", out var vals));
        Assert.AreEqual("1", vals.First());
        Assert.AreEqual("custom", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Head_WithContentLength_HasEmptyBody()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.ContentType = "text/plain";
            ctx.Response.ContentLength = 12;
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Head, server.ListeningHttpsUrl + "/head");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(0, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeResponse_ClearsBody_ToEmpty()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("will-clear"));
        var proxy = suite.GetProxy();
        proxy.BeforeResponse += (_, e) =>
        {
            e.SetResponseBody(Array.Empty<byte>());
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/empty");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_AfterResponse_SeesFinalStatus()
    {
        int? afterStatus = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status201Created;
            return ctx.Response.WriteAsync("created");
        });
        var proxy = suite.GetProxy();
        proxy.AfterResponse += (_, e) =>
        {
            afterStatus = e.HttpClient.Response.StatusCode;
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/created");
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual(201, afterStatus);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeResponse_AppendsMultipleCustomHeaders()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("hdrs"));
        var proxy = suite.GetProxy();
        proxy.BeforeResponse += (_, e) =>
        {
            e.HttpClient.Response.Headers.AddHeader("X-A", "a");
            e.HttpClient.Response.Headers.AddHeader("X-B", "b");
            e.HttpClient.Response.Headers.RemoveHeader("Server");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/hdrs");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("a", response.Headers.GetValues("X-A").First());
        Assert.AreEqual("b", response.Headers.GetValues("X-B").First());
        Assert.AreEqual("hdrs", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeResponse_ReplacesLargeBody()
    {
        var large = new string('Z', 64 * 1024);
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync(large));
        var proxy = suite.GetProxy();
        proxy.BeforeResponse += async (_, e) =>
        {
            var body = await e.GetResponseBodyAsString();
            Assert.AreEqual(large.Length, body.Length);
            e.SetResponseBodyString("small");
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/large");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("small", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Post_BeforeResponse_EchoRewrite()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var reader = new System.IO.StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            await ctx.Response.WriteAsync("echo:" + body);
        });
        var proxy = suite.GetProxy();
        proxy.BeforeResponse += async (_, e) =>
        {
            var body = await e.GetResponseBodyAsString();
            e.SetResponseBodyString(body.ToUpperInvariant());
        };

        using var client = suite.GetClient(proxy);
        using var content = new StringContent("abc", Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync(server.ListeningHttpsUrl + "/echo", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ECHO:ABC", await response.Content.ReadAsStringAsync());
    }
}
