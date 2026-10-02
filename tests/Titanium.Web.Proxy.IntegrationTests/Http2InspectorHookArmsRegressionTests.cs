using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Inspector-hook regression matrix for the non-H3 arms: HTTP/2 client with the Inspector's hook set
///     (<c>GetResponseBody</c> in <c>BeforeResponse</c>, body-write hooks) against an HTTPS origin via the
///     direct arm (origin negotiates h2 or h1 per ALPN) and the forced-HTTP/1.1 bridge arm.
///     Guards the "every request hangs" deadlock (response DATA gated on a handler that awaits that DATA),
///     deferred END_STREAM behind a short client window, and stream/lease cleanup on replaced/synthetic
///     responses.
/// </summary>
[TestClass]
public class Http2InspectorHookArmsRegressionTests
{
    public enum Arm
    {
        Direct,
        ForcedHttp11Bridge
    }

    private static (TestSuite Suite, TestServer Server, ProxyServer Proxy, HttpClient Client) Start(
        Arm arm, bool readResponseBody = true, Action<ProxyServer>? configure = null)
    {
        var suite = new TestSuite();
        var server = suite.GetServer();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.BeforeRequest += (_, _) => Task.CompletedTask;
        proxy.BeforeResponse += async (_, e) =>
        {
            if (readResponseBody && e.HttpClient.Response.HasBody)
                await e.GetResponseBody();
        };
        proxy.OnRequestBodyWrite += (_, _) => Task.CompletedTask;
        proxy.OnResponseBodyWrite += (_, _) => Task.CompletedTask;

        if (arm == Arm.ForcedHttp11Bridge)
        {
            var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
            endpoint.BeforeTunnelConnectRequest += (_, e) =>
            {
                e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
                e.AllowHttpProtocolTranslation = true;
                return Task.CompletedTask;
            };
        }

        configure?.Invoke(proxy);
        var client = TestHelper.GetHttp2Client(proxy);
        client.Timeout = TimeSpan.FromSeconds(20);
        return (suite, server, proxy, client);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(Arm.Direct)]
    [DataRow(Arm.ForcedHttp11Bridge)]
    public async Task Get_Json_WithResponseBodyRead_DeliversBody(Arm arm)
    {
        var (suite, server, _, client) = Start(arm);
        using (suite)
        using (client)
        {
            const string json = "{\"data\":{\"ok\":true}}";
            server.HandleRequest(async ctx =>
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(json);
            });

            var response = await client.GetAsync($"https://localhost:{server.HttpsListeningPort}/graphql");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(json, await response.Content.ReadAsStringAsync());
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(Arm.Direct, true)]
    [DataRow(Arm.Direct, false)]
    [DataRow(Arm.ForcedHttp11Bridge, true)]
    [DataRow(Arm.ForcedHttp11Bridge, false)]
    public async Task Get_LargeBody_ExceedsClientWindow_WithAndWithoutBodyRead(Arm arm, bool readBody)
    {
        {
            var (suite, server, _, client) = Start(arm, readBody);
            using (suite)
            using (client)
            {
                var body = new string('B', 500_000);
                server.HandleRequest(async ctx =>
                {
                    ctx.Response.ContentType = "application/octet-stream";
                    for (var i = 0; i < body.Length; i += 10_000)
                        await ctx.Response.WriteAsync(body.Substring(i, 10_000));
                });

                var response = await client.GetAsync($"https://localhost:{server.HttpsListeningPort}/big");
                var text = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"readBody={readBody}");
                Assert.AreEqual(body.Length, text.Length, $"readBody={readBody}");
            }
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(Arm.Direct)]
    [DataRow(Arm.ForcedHttp11Bridge)]
    public async Task Post_LargeBody_RoundTrips(Arm arm)
    {
        var (suite, server, _, client) = Start(arm);
        using (suite)
        using (client)
        {
            var payload = new string('u', 300_000);
            server.HandleRequest(async ctx =>
            {
                using var reader = new System.IO.StreamReader(ctx.Request.Body);
                var received = await reader.ReadToEndAsync();
                await ctx.Response.WriteAsync($"len={received.Length}");
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"https://localhost:{server.HttpsListeningPort}/post", content);
            Assert.AreEqual($"len={payload.Length}", await response.Content.ReadAsStringAsync());
        }
    }

    [TestMethod]
    [Timeout(90 * 1000)]
    [DataRow(Arm.Direct)]
    [DataRow(Arm.ForcedHttp11Bridge)]
    public async Task ConcurrentRequests_MixedSizes_AllComplete(Arm arm)
    {
        var (suite, server, _, client) = Start(arm);
        using (suite)
        using (client)
        {
            server.HandleRequest(async ctx =>
            {
                var big = ctx.Request.Path.Value!.Contains("big", StringComparison.Ordinal);
                await ctx.Response.WriteAsync(new string('c', big ? 150_000 : 32));
            });

            var tasks = Enumerable.Range(0, 24).Select(async i =>
            {
                var path = i % 3 == 0 ? $"/big/{i}" : $"/s/{i}";
                var response = await client.GetAsync($"https://localhost:{server.HttpsListeningPort}{path}");
                return (i, (await response.Content.ReadAsStringAsync()).Length);
            }).ToArray();

            foreach (var (i, length) in await Task.WhenAll(tasks))
                Assert.AreEqual(i % 3 == 0 ? 150_000 : 32, length, $"request {i}");
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(Arm.Direct)]
    [DataRow(Arm.ForcedHttp11Bridge)]
    public async Task NoContent_And_ErrorStatuses_Complete_And_ConnectionSurvives(Arm arm)
    {
        var (suite, server, _, client) = Start(arm);
        using (suite)
        using (client)
        {
            server.HandleRequest(async ctx =>
            {
                switch (ctx.Request.Path.Value)
                {
                    case "/204":
                        ctx.Response.StatusCode = 204;
                        break;
                    case "/404":
                        ctx.Response.StatusCode = 404;
                        await ctx.Response.WriteAsync("{\"errors\":[]}");
                        break;
                    default:
                        await ctx.Response.WriteAsync("ok");
                        break;
                }
            });

            var baseUrl = $"https://localhost:{server.HttpsListeningPort}";
            Assert.AreEqual(HttpStatusCode.NoContent, (await client.GetAsync(baseUrl + "/204")).StatusCode);
            var notFound = await client.GetAsync(baseUrl + "/404");
            Assert.AreEqual(HttpStatusCode.NotFound, notFound.StatusCode);
            Assert.AreEqual("{\"errors\":[]}", await notFound.Content.ReadAsStringAsync());
            Assert.AreEqual("ok", await (await client.GetAsync(baseUrl + "/after")).Content.ReadAsStringAsync());
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(Arm.Direct)]
    [DataRow(Arm.ForcedHttp11Bridge)]
    public async Task BeforeResponse_Replace_And_BeforeRequest_Synthetic_ThenNormalRequestsWork(Arm arm)
    {
        var (suite, server, _, client) = Start(arm, readResponseBody: false, configure: proxy =>
        {
            proxy.BeforeRequest += (_, e) =>
            {
                if (e.HttpClient.Request.RequestUri.AbsolutePath == "/blocked")
                    e.Ok("synthetic");
                return Task.CompletedTask;
            };
            proxy.BeforeResponse += (_, e) =>
            {
                if (e.HttpClient.Request.RequestUri.AbsolutePath == "/replace")
                    e.Ok("replaced");
                return Task.CompletedTask;
            };
        });
        using (suite)
        using (client)
        {
            server.HandleRequest(async ctx => await ctx.Response.WriteAsync("origin" + ctx.Request.Path));
            var baseUrl = $"https://localhost:{server.HttpsListeningPort}";
            for (var i = 0; i < 8; i++)
            {
                Assert.AreEqual("synthetic", await (await client.GetAsync(baseUrl + "/blocked")).Content.ReadAsStringAsync());
                Assert.AreEqual("replaced", await (await client.GetAsync(baseUrl + "/replace")).Content.ReadAsStringAsync());
                Assert.AreEqual("origin/keep", await (await client.GetAsync(baseUrl + "/keep")).Content.ReadAsStringAsync());
            }
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(Arm.Direct)]
    [DataRow(Arm.ForcedHttp11Bridge)]
    public async Task SlowBody_BeforeResponseGetBody_Completes(Arm arm)
    {
        var (suite, server, _, client) = Start(arm);
        using (suite)
        using (client)
        {
            server.HandleRequest(async ctx =>
            {
                for (var i = 0; i < 5; i++)
                {
                    await ctx.Response.WriteAsync("chunk" + i);
                    await ctx.Response.Body.FlushAsync();
                    await Task.Delay(100);
                }
            });

            var response = await client.GetAsync($"https://localhost:{server.HttpsListeningPort}/slow");
            Assert.AreEqual("chunk0chunk1chunk2chunk3chunk4", await response.Content.ReadAsStringAsync());
        }
    }

    [TestMethod]
    [Timeout(120 * 1000)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task LargeBody_BufferedProxyThenStreamingProxy_InOneTest_BothComplete(int instance)
    {
        // Two proxies back to back in one process (buffered GetResponseBody first, then streaming hook only):
        // the second used to stall intermittently on the body-write hook path (lost WINDOW_UPDATE wakeup).
        _ = instance;
        var body = new string('B', 500_000);
        foreach (var readBody in new[] { true, false })
        {
            var (suite, server, _, client) = Start(Arm.Direct, readBody);
            using (suite)
            using (client)
            {
                server.HandleRequest(async ctx =>
                {
                    for (var i = 0; i < body.Length; i += 10_000)
                        await ctx.Response.WriteAsync(body.Substring(i, 10_000));
                });

                var response = await client.GetAsync($"https://localhost:{server.HttpsListeningPort}/big");
                var text = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(body.Length, text.Length, $"readBody={readBody}");
            }
        }
    }
}