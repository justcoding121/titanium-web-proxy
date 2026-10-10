#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression matrix for the x.com failures seen through the Inspector: a browser (HTTP/2 client) talking
///     to an HTTP/3 origin (api.x.com advertises Alt-Svc) through the MITM bridge with the Inspector's hook
///     set attached (<c>BeforeRequest</c>, <c>BeforeResponse</c> + <c>GetResponseBody</c>,
///     <c>OnRequestBodyWrite</c>, <c>OnResponseBodyWrite</c>). Failure modes covered:
///     <list type="bullet">
///         <item>QuicStream disposed before the response body writer ran (<c>ObjectDisposedException</c>);</item>
///         <item>response DATA gated on a handler that waits for that DATA (every request hangs);</item>
///         <item>deferred END_STREAM lost behind a short client window (body never ends);</item>
///         <item>origin stream / lease leaks when BeforeResponse replaces the response or a request is synthetic.</item>
///     </list>
///     Every scenario runs on both the cold (CONNECT-time bridge) and warm (capability-cache) entry paths.
/// </summary>
[TestClass]
public class Http2ToHttp3InspectorRegressionTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    /// <summary>Same hook set the Inspector installs (all no-op unless a test overrides behaviour).</summary>
    private static void AttachInspectorLikeHooks(ProxyServer proxy, bool readResponseBody = true)
    {
        proxy.BeforeRequest += (_, _) => Task.CompletedTask;
        proxy.BeforeResponse += async (_, e) =>
        {
            if (readResponseBody && e.HttpClient.Response.HasBody)
                await e.GetResponseBody();
        };
        proxy.OnRequestBodyWrite += (_, _) => Task.CompletedTask;
        proxy.OnResponseBodyWrite += (_, _) => Task.CompletedTask;
    }

    private static void RouteToHttp3(ProxyServer proxy, QuicHttp3OriginServer origin, bool warm)
    {
        proxy.EnableHttp2 = true;
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;

        if (warm)
        {
            proxy.Http3OriginCapabilityCache.Set($"localhost:{origin.Port}", int.MinValue,
                TimeSpan.FromMinutes(5), targetName: null);
            proxy.Http3WarmOrigins.Mark("localhost", origin.Port);
            proxy.BeforeRequest += (_, args) =>
            {
                args.UpstreamHttpProtocol = UpstreamHttpProtocol.Auto;
                return Task.CompletedTask;
            };
        }
        else
        {
            var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
            endpoint.BeforeTunnelConnectRequest += (_, e) =>
            {
                e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
                e.AllowHttpProtocolTranslation = true;
                return Task.CompletedTask;
            };
        }
    }

    private static (TestSuite Suite, ProxyServer Proxy, HttpClient Client) Start(
        QuicHttp3OriginServer origin, bool warm, bool readResponseBody = true)
    {
        var suite = new TestSuite();
        var proxy = suite.GetProxy();
        AttachInspectorLikeHooks(proxy, readResponseBody);
        RouteToHttp3(proxy, origin, warm);
        var client = TestHelper.GetHttp2Client(proxy);
        client.Timeout = TimeSpan.FromSeconds(20);
        return (suite, proxy, client);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Get_Json_WithResponseBodyRead_DeliversBody(bool warm)
    {
        RequireQuic();
        const string json = "{\"data\":{\"viewer\":{\"id\":\"1\"}}}";
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(
            new QuicHttp3Response(200, json, Encoding.UTF8.GetBytes(json), contentType: "application/json")));

        var (suite, _, client) = Start(origin, warm);
        using (suite)
        using (client)
        {
            var response = await client.GetAsync($"https://localhost:{origin.Port}/graphql");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(json, await response.Content.ReadAsStringAsync());
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Get_WithoutResponseBodyRead_StreamsThroughBodyHook(bool warm)
    {
        RequireQuic();
        var body = new string('s', 150_000);
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, body,
            Encoding.UTF8.GetBytes(body), dataFrameSize: 8 * 1024)));

        var (suite, _, client) = Start(origin, warm, readResponseBody: false);
        using (suite)
        using (client)
        {
            var response = await client.GetAsync($"https://localhost:{origin.Port}/stream");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(body, await response.Content.ReadAsStringAsync());
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Get_LargeMultiFrameBody_WithResponseBodyRead_ExceedsClientWindow(bool warm)
    {
        RequireQuic();
        var body = new string('L', 400_000);
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, body,
            Encoding.UTF8.GetBytes(body), dataFrameSize: 16 * 1024)));

        var (suite, _, client) = Start(origin, warm);
        using (suite)
        using (client)
        {
            var response = await client.GetAsync($"https://localhost:{origin.Port}/large");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(body.Length, (await response.Content.ReadAsStringAsync()).Length);
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Post_JsonBody_RoundTrips_WithBodyWriteHooks(bool warm)
    {
        RequireQuic();
        var payload = new string('p', 70_000); // > default 64 KiB stream window
        byte[]? seen = null;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seen = req.Body;
            return Task.FromResult(new QuicHttp3Response(200, $"len={req.Body.Length}"));
        });

        var (suite, _, client) = Start(origin, warm);
        using (suite)
        using (client)
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"https://localhost:{origin.Port}/post", content);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual($"len={payload.Length}", await response.Content.ReadAsStringAsync());
            Assert.AreEqual(payload, Encoding.UTF8.GetString(seen!));
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ManySequentialRequests_OnOneConnection_ReleaseOriginStreams(bool warm)
    {
        RequireQuic();
        var counter = 0;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, $"r{Interlocked.Increment(ref counter)}")));

        var (suite, _, client) = Start(origin, warm);
        using (suite)
        using (client)
        {
            for (var i = 0; i < 40; i++)
            {
                var response = await client.GetAsync($"https://localhost:{origin.Port}/n/{i}");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"request {i}");
                Assert.IsTrue((await response.Content.ReadAsStringAsync()).StartsWith('r'), $"request {i}");
            }
        }

        Assert.AreEqual(40, counter);
    }

    [TestMethod]
    [Timeout(90 * 1000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcurrentRequests_MixedSizes_AllComplete(bool warm)
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            var size = req.Path.Contains("big", StringComparison.Ordinal) ? 120_000 : 64;
            var text = new string('c', size);
            return Task.FromResult(new QuicHttp3Response(200, text, Encoding.UTF8.GetBytes(text),
                dataFrameSize: 8 * 1024));
        });

        var (suite, _, client) = Start(origin, warm);
        using (suite)
        using (client)
        {
            var tasks = Enumerable.Range(0, 24).Select(async i =>
            {
                var path = i % 4 == 0 ? $"/big/{i}" : $"/small/{i}";
                var response = await client.GetAsync($"https://localhost:{origin.Port}{path}");
                var text = await response.Content.ReadAsStringAsync();
                return (i, response.StatusCode, text.Length);
            }).ToArray();

            var results = await Task.WhenAll(tasks);
            foreach (var (i, status, length) in results)
            {
                Assert.AreEqual(HttpStatusCode.OK, status, $"request {i}");
                Assert.AreEqual(i % 4 == 0 ? 120_000 : 64, length, $"request {i}");
            }
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmptyAndErrorResponses_Complete(bool warm)
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req => Task.FromResult(req.Path switch
        {
            "/204" => new QuicHttp3Response(204, string.Empty, Array.Empty<byte>()),
            "/404" => new QuicHttp3Response(404, "{\"errors\":[{\"code\":34}]}"),
            "/429" => new QuicHttp3Response(429, "rate", Encoding.UTF8.GetBytes("rate"),
                new[] { ("retry-after", "1") }),
            _ => new QuicHttp3Response(200, "ok")
        }));

        var (suite, _, client) = Start(origin, warm);
        using (suite)
        using (client)
        {
            var noContent = await client.GetAsync($"https://localhost:{origin.Port}/204");
            Assert.AreEqual(HttpStatusCode.NoContent, noContent.StatusCode);
            Assert.AreEqual(0, (await noContent.Content.ReadAsByteArrayAsync()).Length);

            var notFound = await client.GetAsync($"https://localhost:{origin.Port}/404");
            Assert.AreEqual(HttpStatusCode.NotFound, notFound.StatusCode);
            StringAssert.Contains(await notFound.Content.ReadAsStringAsync(), "errors");

            var limited = await client.GetAsync($"https://localhost:{origin.Port}/429");
            Assert.AreEqual((HttpStatusCode)429, limited.StatusCode);
            Assert.AreEqual("1", limited.Headers.GetValues("retry-after").Single());

            // The connection must still be healthy afterwards.
            var ok = await client.GetAsync($"https://localhost:{origin.Port}/after");
            Assert.AreEqual("ok", await ok.Content.ReadAsStringAsync());
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BeforeResponse_ReplacesResponse_ReleasesOriginStream_NextRequestWorks(bool warm)
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req => Task.FromResult(new QuicHttp3Response(200, "origin:" + req.Path,
            Encoding.UTF8.GetBytes("origin:" + req.Path), dataFrameSize: 4 * 1024)));

        var suite = new TestSuite();
        var proxy = suite.GetProxy();
        AttachInspectorLikeHooks(proxy, readResponseBody: false);
        proxy.BeforeResponse += (_, e) =>
        {
            if (e.HttpClient.Request.RequestUri.AbsolutePath == "/replace")
                e.Ok("replaced");
            return Task.CompletedTask;
        };
        RouteToHttp3(proxy, origin, warm);
        using (suite)
        using (var client = TestHelper.GetHttp2Client(proxy))
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            for (var i = 0; i < 10; i++)
            {
                var replaced = await client.GetAsync($"https://localhost:{origin.Port}/replace");
                Assert.AreEqual("replaced", await replaced.Content.ReadAsStringAsync(), $"iteration {i}");
                var passthrough = await client.GetAsync($"https://localhost:{origin.Port}/keep");
                Assert.AreEqual("origin:/keep", await passthrough.Content.ReadAsStringAsync(), $"iteration {i}");
            }
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BeforeRequest_SyntheticResponse_DoesNotTouchOrigin_AndConnectionSurvives(bool warm)
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "origin")));

        var suite = new TestSuite();
        var proxy = suite.GetProxy();
        AttachInspectorLikeHooks(proxy);
        proxy.BeforeRequest += (_, e) =>
        {
            if (e.HttpClient.Request.RequestUri.AbsolutePath == "/blocked")
                e.Ok("synthetic");
            return Task.CompletedTask;
        };
        RouteToHttp3(proxy, origin, warm);
        using (suite)
        using (var client = TestHelper.GetHttp2Client(proxy))
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            var blocked = await client.GetAsync($"https://localhost:{origin.Port}/blocked");
            Assert.AreEqual("synthetic", await blocked.Content.ReadAsStringAsync());
            var real = await client.GetAsync($"https://localhost:{origin.Port}/real");
            Assert.AreEqual("origin", await real.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    ///     The other tests only look at what the client receives, which hides a failing
    ///     <c>GetResponseBody</c> (the proxy swallows handler exceptions and still streams the body). The
    ///     Inspector lost the status / origin protocol of every H2→H3 response with a body because of that
    ///     ("Connection is null": the H1 reader was used for a QUIC-backed body). Assert the handler itself
    ///     sees the body, and the client still gets it afterwards.
    /// </summary>
    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false, 64, false)]
    [DataRow(true, 64, false)]
    [DataRow(false, 150_000, false)]
    [DataRow(true, 150_000, false)]
    [DataRow(false, 6_000, true)]
    [DataRow(true, 6_000, true)]
    public async Task BeforeResponse_GetResponseBody_ReturnsOriginBody_AndClientStillReceivesIt(
        bool warm, int size, bool gzipped)
    {
        RequireQuic();
        var text = new string('b', size);
        var wire = Encoding.UTF8.GetBytes(text);
        var headers = Array.Empty<(string, string)>();
        if (gzipped)
        {
            using var ms = new MemoryStream();
            using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(wire);
            wire = ms.ToArray();
            headers = new[] { ("content-encoding", "gzip") };
        }

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, string.Empty, wire, headers,
            "text/plain", dataFrameSize: 8 * 1024)));

        var suite = new TestSuite();
        var proxy = suite.GetProxy();
        AttachInspectorLikeHooks(proxy, readResponseBody: false);
        string? seenByHandler = null;
        Exception? handlerFailure = null;
        Version? seenVersion = null;
        proxy.BeforeResponse += async (_, e) =>
        {
            try
            {
                seenVersion = e.HttpClient.Response.HttpVersion;
                seenByHandler = await e.GetResponseBodyAsString();
            }
            catch (Exception ex)
            {
                handlerFailure = ex;
            }
        };
        RouteToHttp3(proxy, origin, warm);
        using (suite)
        using (var client = TestHelper.GetHttp2Client(proxy))
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            var response = await client.GetAsync($"https://localhost:{origin.Port}/body");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync();

            Assert.IsNull(handlerFailure, $"GetResponseBody threw: {handlerFailure}");
            Assert.AreEqual(HttpVersion.Version30, seenVersion, "origin leg should be HTTP/3");
            Assert.AreEqual(text, seenByHandler, "BeforeResponse must see the (decoded) origin body");

            var delivered = response.Content.Headers.ContentEncoding.Contains("gzip")
                ? Decode(bytes)
                : Encoding.UTF8.GetString(bytes);
            Assert.AreEqual(text, delivered, "client must still receive the full body");

            // The origin stream must have been released: the connection keeps serving requests.
            var again = await client.GetAsync($"https://localhost:{origin.Port}/again");
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
        }

        static string Decode(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GzipJson_WithResponseBodyRead_IsDecodableByClient(bool warm)
    {
        RequireQuic();
        var json = "{\"items\":[" + string.Join(",", Enumerable.Range(0, 2000).Select(i => $"{{\"i\":{i}}}")) + "]}";
        byte[] gz;
        using (var ms = new MemoryStream())
        {
            using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(Encoding.UTF8.GetBytes(json));
            gz = ms.ToArray();
        }

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, string.Empty, gz,
            new[] { ("content-encoding", "gzip") }, "application/json", dataFrameSize: 4 * 1024)));

        var (suite, _, client) = Start(origin, warm);
        using (suite)
        using (client)
        {
            var response = await client.GetAsync($"https://localhost:{origin.Port}/gz");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            // HttpClient (no AutomaticDecompression) sees the wire bytes; either encoded or already
            // decoded by the proxy is acceptable, but the payload must be intact JSON when decoded.
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var decoded = response.Content.Headers.ContentEncoding.Contains("gzip")
                ? Decode(bytes)
                : Encoding.UTF8.GetString(bytes);
            Assert.AreEqual(json, decoded);
        }

        static string Decode(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ColdBridge_OriginClosedMidStream_DoesNotHangClient()
    {
        RequireQuic();
        var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        var port = origin.Port;
        origin.HandleRequest(async request =>
        {
            // Give the proxy time to commit HEADERS, then drop the whole listener.
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                await origin.DisposeAsync();
            });
            await Task.Delay(500);
            return new QuicHttp3Response(200, "late");
        });

        var (suite, _, client) = Start(origin, warm: false);
        using (suite)
        using (client)
        {
            // Any outcome other than hanging is acceptable: a response or a clean failure.
            try
            {
                using var response = await client.GetAsync($"https://localhost:{port}/die");
                _ = await response.Content.ReadAsStringAsync();
            }
            catch (HttpRequestException)
            {
            }
            catch (IOException)
            {
            }
        }
    }
}
