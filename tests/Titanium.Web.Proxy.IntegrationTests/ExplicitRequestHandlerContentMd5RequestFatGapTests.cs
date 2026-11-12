using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: request Content-MD5 on PUT reaches the origin.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerContentMd5RequestFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_RequestContentMd5_OnPut_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            if (ctx.Request.Headers.TryGetValue("Content-MD5", out var md5))
                seen = md5.ToString();
            var body = await new System.IO.StreamReader(ctx.Request.Body).ReadToEndAsync();
            Assert.AreEqual("put-gap", body);
            await ctx.Response.WriteAsync("ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var content = new StringContent("put-gap", Encoding.UTF8, "text/plain");
        content.Headers.ContentMD5 = Convert.FromBase64String("oQIbx75fpk+celtKObNI3g==");
        using var request = new HttpRequestMessage(HttpMethod.Put, server.ListeningHttpsUrl + "/md5-req")
        {
            Content = content,
        };
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("oQIbx75fpk+celtKObNI3g==", seen);
    }
}
