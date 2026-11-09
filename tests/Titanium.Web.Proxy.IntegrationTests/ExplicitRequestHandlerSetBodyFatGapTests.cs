using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: BeforeRequest SetRequestBodyString, empty body replace,
///     and GenericResponse 418 short-circuit.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerSetBodyFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: SetRequestBodyString is ignored and the original body reaches the origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_SetRequestBodyString_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            seen = await reader.ReadToEndAsync();
            await ctx.Response.WriteAsync("body-ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.SetRequestBodyString("replaced-body");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var content = new StringContent("original-body", Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync(server.ListeningHttpsUrl + "/set-body", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("replaced-body", seen);
        Assert.AreEqual("body-ok", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    ///     Bug class: SetRequestBody(empty) leaves the original POST body.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_SetRequestBodyEmpty_ClearsBody()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            seen = await reader.ReadToEndAsync();
            await ctx.Response.WriteAsync("empty-ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.SetRequestBody(Array.Empty<byte>());
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var content = new StringContent("will-clear", Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync(server.ListeningHttpsUrl + "/empty-body", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(string.Empty, seen);
    }

    /// <summary>
    ///     Bug class: GenericResponse 418 still dials the origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_GenericResponse418_SkipsOrigin()
    {
        var contacted = false;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(_ =>
        {
            contacted = true;
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.GenericResponse("teapot", (HttpStatusCode)418);
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/tea");
        Assert.AreEqual((HttpStatusCode)418, response.StatusCode);
        Assert.AreEqual("teapot", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(contacted);
    }
}
