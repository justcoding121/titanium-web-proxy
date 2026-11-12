using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat H1TerminateFastForward / ExplicitClientHandler / ResponseHandler suite:
///     explicit H1 terminate GET/POST, AfterResponse, GenericResponse cancel,
///     UserData round-trip, Expect-100 short-circuit, and path rewrite.
/// </summary>
[DoNotParallelize]
[TestClass]
public class H1TerminateExplicitResponseFatGapTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1Terminate_GetAndPost_ViaExplicit_RoundTrip()
    {
        // Hits H1TerminateFastForward when EnableHttp2 is off on the explicit MITM path.
        string? seenBody = null;
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            if (HttpMethods.IsPost(ctx.Request.Method))
            {
                using var reader = new StreamReader(ctx.Request.Body);
                seenBody = await reader.ReadToEndAsync();
            }

            await ctx.Response.WriteAsync("h1-term-ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);

        using (var get = await client.GetAsync(server.ListeningHttpsUrl + "/get"))
        {
            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
            Assert.AreEqual("h1-term-ok", await get.Content.ReadAsStringAsync());
        }

        using (var content = new StringContent("post-body", Encoding.UTF8, "text/plain"))
        using (var post = await client.PostAsync(server.ListeningHttpsUrl + "/post", content))
        {
            Assert.AreEqual(HttpStatusCode.OK, post.StatusCode);
            Assert.AreEqual("post-body", seenBody);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_AfterResponse_FiresOnce()
    {
        var after = 0;
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("after"));
        var proxy = suite.GetProxy();
        proxy.AfterResponse += (_, _) =>
        {
            Interlocked.Increment(ref after);
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/after");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        for (var i = 0; i < 50 && Volatile.Read(ref after) == 0; i++)
            await Task.Delay(50);
        Assert.AreEqual(1, Volatile.Read(ref after));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_CancelRequest_SkipsOrigin()
    {
        var contacted = false;
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(_ =>
        {
            contacted = true;
            return Task.CompletedTask;
        });
        var proxy = suite.GetProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.GenericResponse("cancelled", HttpStatusCode.Conflict);
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/cancel");
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.AreEqual("cancelled", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(contacted);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_UserData_RoundTripsAcrossHooks()
    {
        object? seen = null;
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("ud"));
        var proxy = suite.GetProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.UserData = "marker-42";
            return Task.CompletedTask;
        };
        proxy.BeforeResponse += (_, e) =>
        {
            seen = e.UserData;
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/ud");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("marker-42", seen as string);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_ExpectContinue_BeforeRequest_Ok_SkipsOrigin()
    {
        var contacted = false;
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(_ =>
        {
            contacted = true;
            return Task.CompletedTask;
        });
        var proxy = suite.GetProxy();
        proxy.Enable100ContinueBehaviour = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("no-continue");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var content = new StringContent("body", Encoding.UTF8, "text/plain");
        using var request = new HttpRequestMessage(HttpMethod.Post, server.ListeningHttpsUrl + "/cont")
        {
            Content = content,
            Version = HttpVersion.Version11,
        };
        request.Headers.ExpectContinue = true;
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("no-continue", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(contacted);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_RewritesPath()
    {
        string? seenPath = null;
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seenPath = ctx.Request.Path;
            return ctx.Response.WriteAsync("rewritten");
        });
        var proxy = suite.GetProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.RequestUri = new Uri(server.ListeningHttpsUrl + "/new-path");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/old-path");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("/new-path", seenPath);
    }
}
