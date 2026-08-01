using System;
using System.Collections.Generic;
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

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Http2_Tls_MultiplexedStreams_ShareServerConnectionId_WithoutHasConnection()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var serverIds = new List<Guid>();
        var clientIds = new List<Guid>();
        var hasConnectionFlags = new List<bool>();
        proxy.BeforeResponse += (_, args) =>
        {
            lock (serverIds)
            {
                serverIds.Add(args.ServerConnectionId);
                clientIds.Add(args.ClientConnectionId);
                hasConnectionFlags.Add(args.HttpClient.HasConnection);
            }

            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        var urls = Enumerable.Range(0, 3)
            .Select(i => server.ListeningHttpsUrl.TrimEnd('/') + "/id-" + i)
            .ToArray();
        var responses = await Task.WhenAll(urls.Select(u => client.GetAsync(u)));
        foreach (var response in responses)
        {
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }

        for (var i = 0; i < 50 && serverIds.Count < 3; i++) await Task.Delay(20);

        Assert.AreEqual(3, serverIds.Count);
        Assert.IsTrue(serverIds.All(id => id != Guid.Empty), "each H2 stream should expose a non-empty ServerConnectionId");
        Assert.AreEqual(1, serverIds.Distinct().Count(), "multiplexed H2 streams should share one upstream connection id");
        Assert.AreEqual(1, clientIds.Distinct().Count(), "multiplexed H2 streams should share one client connection id");
        Assert.IsTrue(hasConnectionFlags.All(v => !v),
            "native H2 must BindUpstreamConnectionId only — HasConnection must stay false");
    }
}
