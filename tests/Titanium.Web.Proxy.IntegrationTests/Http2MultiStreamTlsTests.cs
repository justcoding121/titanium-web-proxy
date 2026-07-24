using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Closest reliable characterization for issue #838 (repeated gRPC-over-TLS / multi-stream HTTP/2):
///     multiple concurrent and sequential HTTP/2 streams on one TLS connection through a decrypting proxy.
/// </summary>
[TestClass]
public class Http2MultiStreamTlsTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Http2_Tls_MultipleConcurrentAndSequentialStreams_Succeed()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        var hits = 0;
        server.HandleRequest(async context =>
        {
            System.Threading.Interlocked.Increment(ref hits);
            var path = context.Request.Path.Value ?? "/";
            await context.Response.WriteAsync("stream-ok:" + path);
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        using var client = TestHelper.GetHttp2Client(proxy);

        var paths = new[] { "/a", "/b", "/c", "/d" };
        var concurrent = paths.Select(p => client.GetAsync(server.ListeningHttpsUrl.TrimEnd('/') + p)).ToArray();
        var responses = await Task.WhenAll(concurrent);

        foreach (var (response, path) in responses.Zip(paths))
        {
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, path);
            Assert.AreEqual(new Version(2, 0), response.Version, path);
            Assert.AreEqual("stream-ok:" + path, await response.Content.ReadAsStringAsync());
            response.Dispose();
        }

        // Sequential follow-ups on the same HTTP/2 connection (gRPC often issues call 2 after call 1).
        for (var i = 0; i < 4; i++)
        {
            var path = "/seq-" + i;
            using var response = await client.GetAsync(server.ListeningHttpsUrl.TrimEnd('/') + path);
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("stream-ok:" + path, await response.Content.ReadAsStringAsync());
        }

        Assert.AreEqual(8, hits, "All concurrent and sequential streams should have hit the origin.");
    }
}
