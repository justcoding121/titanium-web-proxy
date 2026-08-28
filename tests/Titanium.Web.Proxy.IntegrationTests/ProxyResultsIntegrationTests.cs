using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

[DoNotParallelize]
[TestClass]
public class ProxyResultsIntegrationTests
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
    public async Task ProxyResults_Json_From_BeforeRequest_Blocks_With_Forbidden()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var originContacted = false;
        server.HandleRequest(context =>
        {
            originContacted = true;
            return context.Response.WriteAsync("origin");
        });

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.Respond(ProxyResults.Json(new { error = "blocked" }, HttpStatusCode.Forbidden));
            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        StringAssert.Contains(body, "blocked");
        Assert.IsFalse(originContacted);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task ProxyResults_Redirect_Uses_Custom_Status()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("origin"));

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.Respond(ProxyResults.Redirect("https://example.invalid/moved", HttpStatusCode.MovedPermanently));
            return Task.CompletedTask;
        };

        var handler = new HttpClientHandler
        {
            Proxy = new TestHelper.TestProxy($"http://localhost:{proxy.ProxyEndPoints[0].Port}", false),
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler);
        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));

        Assert.AreEqual(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.AreEqual("https://example.invalid/moved", response.Headers.Location?.ToString());
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task ProxyResults_Stream_From_BeforeRequest_Writes_Body()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var originContacted = false;
        server.HandleRequest(context =>
        {
            originContacted = true;
            return context.Response.WriteAsync("origin");
        });

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.RespondStreaming(ProxyResults.Stream(
                HttpStatusCode.OK,
                "text/plain",
                async (stream, ct) => await stream.WriteAsync("streamed-body"u8.ToArray(), ct)), closeServerConnection: false);
            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("streamed-body", body);
        Assert.IsFalse(originContacted);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task ProxyResults_File_From_BeforeRequest_Serves_File()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "twp-proxyresults-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(tempFile, "file-body");

        try
        {
            using var testSuite = new TestSuite(sharedServer);
            var server = testSuite.GetServer();
            server.HandleRequest(context => context.Response.WriteAsync("origin"));

            var proxy = testSuite.GetProxy();
            proxy.BeforeRequest += (_, e) =>
            {
                e.RespondStreaming(ProxyResults.File(tempFile, "text/plain"), closeServerConnection: false);
                return Task.CompletedTask;
            };

            using var client = testSuite.GetClient(proxy);
            var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("file-body", body);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
