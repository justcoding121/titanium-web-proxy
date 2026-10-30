using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Caching;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
/// The response cache against real sockets, on every response path the proxy has: the session-less terminate-lite
/// path, the lite path with a session, and the full session path.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ResponseCachePathsTests
{
    public enum PathMode
    {
        /// <summary>No handlers: terminate-lite without a session.</summary>
        Lite,

        /// <summary>A BeforeRequest handler: terminate-lite with a session bag.</summary>
        LiteSession,

        /// <summary>A BeforeResponse handler: the full session response handler.</summary>
        Full,

        /// <summary>An explicit forward-proxy endpoint: the session goes through the regular request and response handlers.</summary>
        Explicit,
    }

    public static IEnumerable<object[]> Modes()
    {
        yield return [PathMode.Lite];
        yield return [PathMode.LiteSession];
        yield return [PathMode.Full];
        yield return [PathMode.Explicit];
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IHost _origin;
        private int _hits;
        private int _conditionalHits;
        private readonly HttpClient _client;
        private readonly int _originPort;
        private readonly bool _explicit;

        public Fixture(PathMode mode, Func<HttpContext, Task> origin, ResponseCacheOptions? options = null)
        {
            _origin = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureWebHostDefaults(web =>
                {
                    web.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
                    web.Configure(app => app.Run(async context =>
                    {
                        Interlocked.Increment(ref _hits);
                        if (context.Request.Headers.ContainsKey("If-None-Match") ||
                            context.Request.Headers.ContainsKey("If-Modified-Since"))
                        {
                            Interlocked.Increment(ref _conditionalHits);
                        }

                        await origin(context);
                    }));
                })
                .Build();
            _origin.Start();
            var originPort = new Uri(_origin.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;

            Proxy = new ProxyServer(false, false, false);
            _originPort = originPort;
            _explicit = mode == PathMode.Explicit;
            Cache = new MemoryHttpResponseCache();
            if (_explicit)
            {
                Proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
            }
            else
            {
                Proxy.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
                var endpoint = Proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
                endpoint.ForwardHost = "127.0.0.1";
                endpoint.ForwardPort = originPort;
                endpoint.ForwardCleartext = true;
            }

            Proxy.ReverseProxy = new ReverseProxyOptions
            {
                Middleware = [new HttpResponseCacheMiddleware(Cache, options: options ?? new ResponseCacheOptions { ExposeHeaders = true })],
            };

            switch (mode)
            {
                case PathMode.LiteSession:
                    Proxy.BeforeRequest += (_, _) => Task.CompletedTask;
                    break;
                case PathMode.Full:
                    Proxy.BeforeResponse += (_, _) => Task.CompletedTask;
                    break;
            }

            Proxy.Start();
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None };
            if (_explicit)
            {
                handler.UseProxy = true;
                handler.Proxy = new WebProxy($"http://127.0.0.1:{Proxy.ProxyEndPoints[0].Port}");
            }

            _client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
        }
        public ProxyServer Proxy { get; }

        public MemoryHttpResponseCache Cache { get; }

        public int Hits => Volatile.Read(ref _hits);

        public int ConditionalHits => Volatile.Read(ref _conditionalHits);

        public string Url(string path) => $"http://127.0.0.1:{(_explicit ? _originPort : Proxy.ProxyEndPoints[0].Port)}{path}";

        public Task<HttpResponseMessage> GetAsync(string path, params (string Name, string Value)[] headers)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Url(path));
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            return _client.SendAsync(request);
        }

        public Task<HttpResponseMessage> HeadAsync(string path) =>
            _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Url(path)));

        public void Dispose()
        {
            _client.Dispose();
            Proxy.Stop();
            Proxy.Dispose();
            _origin.StopAsync().Wait();
            _origin.Dispose();
        }
    }

    private static string XCache(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Cache", out var values) ? string.Join(",", values) : string.Empty;

    [TestMethod]
    [DynamicData(nameof(Modes))]
    [Timeout(60 * 1000)]
    public async Task SmallBody_MissThenHit_OriginSeenOnce(PathMode mode)
    {
        using var f = new Fixture(mode, async context =>
        {
            context.Response.Headers.CacheControl = "max-age=60";
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("small-body");
        });

        var first = await f.GetAsync("/small");
        Assert.AreEqual("small-body", await first.Content.ReadAsStringAsync());
        Assert.AreEqual(string.Empty, XCache(first), "a miss carries no X-Cache");

        var second = await f.GetAsync("/small");
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual("small-body", await second.Content.ReadAsStringAsync());
        Assert.AreEqual("HIT", XCache(second));
        Assert.AreEqual("text/plain", second.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(10, second.Content.Headers.ContentLength);
        Assert.AreEqual(1, f.Hits);
    }

    [TestMethod]
    [DynamicData(nameof(Modes))]
    [Timeout(60 * 1000)]
    public async Task LargeKnownLength_BeyondTheCoalesceLimit_IsStoredByTee(PathMode mode)
    {
        var payload = new byte[200 * 1024];
        new Random(7).NextBytes(payload);
        using var f = new Fixture(mode, async context =>
        {
            context.Response.Headers.CacheControl = "max-age=60";
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = payload.Length;
            await context.Response.Body.WriteAsync(payload);
        });

        var first = await f.GetAsync("/large");
        CollectionAssert.AreEqual(payload, await first.Content.ReadAsByteArrayAsync());
        var second = await f.GetAsync("/large");
        Assert.AreEqual("HIT", XCache(second));
        CollectionAssert.AreEqual(payload, await second.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(1, f.Hits);
    }

    [TestMethod]
    [DynamicData(nameof(Modes))]
    [Timeout(60 * 1000)]
    public async Task ChunkedBody_IsStoredAndServedWithAContentLength(PathMode mode)
    {
        using var f = new Fixture(mode, async context =>
        {
            context.Response.Headers.CacheControl = "max-age=60";
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("chunk-one;");
            await context.Response.Body.FlushAsync();
            await Task.Delay(20);
            await context.Response.WriteAsync("chunk-two");
        });

        var first = await f.GetAsync("/chunked");
        Assert.AreEqual("chunk-one;chunk-two", await first.Content.ReadAsStringAsync());
        var second = await f.GetAsync("/chunked");
        Assert.AreEqual("HIT", XCache(second));
        Assert.AreEqual("chunk-one;chunk-two", await second.Content.ReadAsStringAsync());
        Assert.AreEqual(19, second.Content.Headers.ContentLength);
        Assert.AreEqual(1, f.Hits);
    }

    [TestMethod]
    [DynamicData(nameof(Modes))]
    [Timeout(60 * 1000)]
    public async Task BodyOverTheEntryLimit_StreamsThroughAndIsNotCached(PathMode mode)
    {
        var payload = new byte[64 * 1024];
        new Random(3).NextBytes(payload);
        using var f = new Fixture(mode, async context =>
        {
            context.Response.Headers.CacheControl = "max-age=60";
            context.Response.ContentLength = payload.Length;
            await context.Response.Body.WriteAsync(payload);
        }, new ResponseCacheOptions { MaxEntryBytes = 16 * 1024, ExposeHeaders = true });

        for (var i = 0; i < 2; i++)
        {
            var response = await f.GetAsync("/too-big");
            CollectionAssert.AreEqual(payload, await response.Content.ReadAsByteArrayAsync());
            Assert.AreEqual(string.Empty, XCache(response));
        }

        Assert.AreEqual(2, f.Hits);
        Assert.AreEqual(0, f.Cache.Count);
    }

    [TestMethod]
    [DynamicData(nameof(Modes))]
    [Timeout(60 * 1000)]
    public async Task ContentEncodedBody_IsServedByteForByte_NeverRecompressed(PathMode mode)
    {
        byte[] gzip;
        using (var ms = new MemoryStream())
        {
            using (var zip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                zip.Write(Encoding.UTF8.GetBytes(new string('g', 2000)));
            }

            gzip = ms.ToArray();
        }

        using var f = new Fixture(mode, async context =>
        {
            context.Response.Headers.CacheControl = "max-age=60";
            context.Response.Headers.ContentEncoding = "gzip";
            context.Response.Headers.Vary = "Accept-Encoding";
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength = gzip.Length;
            await context.Response.Body.WriteAsync(gzip);
        });

        var first = await f.GetAsync("/gz", ("Accept-Encoding", "gzip"));
        CollectionAssert.AreEqual(gzip, await first.Content.ReadAsByteArrayAsync());
        var second = await f.GetAsync("/gz", ("Accept-Encoding", "gzip"));
        Assert.AreEqual("HIT", XCache(second));
        CollectionAssert.AreEqual(gzip, await second.Content.ReadAsByteArrayAsync());
        Assert.AreEqual("gzip", second.Content.Headers.ContentEncoding.Single());
        Assert.AreEqual(gzip.Length, second.Content.Headers.ContentLength);

        var identity = await f.GetAsync("/gz", ("Accept-Encoding", "identity"));
        Assert.AreEqual(string.Empty, XCache(identity), "a different Accept-Encoding is a different variant");
        Assert.AreEqual(2, f.Hits);
    }

    [TestMethod]
    [DynamicData(nameof(Modes))]
    [Timeout(60 * 1000)]
    public async Task Vary_KeepsVariantsApart(PathMode mode)
    {
        using var f = new Fixture(mode, async context =>
        {
            var language = context.Request.Headers.AcceptLanguage.ToString();
            context.Response.Headers.CacheControl = "max-age=60";
            context.Response.Headers.Vary = "Accept-Language";
            await context.Response.WriteAsync("lang=" + language);
        });

        Assert.AreEqual("lang=en", await (await f.GetAsync("/v", ("Accept-Language", "en"))).Content.ReadAsStringAsync());
        Assert.AreEqual("lang=fr", await (await f.GetAsync("/v", ("Accept-Language", "fr"))).Content.ReadAsStringAsync());
        var en = await f.GetAsync("/v", ("Accept-Language", "en"));
        var fr = await f.GetAsync("/v", ("Accept-Language", "fr"));
        Assert.AreEqual("HIT", XCache(en));
        Assert.AreEqual("lang=en", await en.Content.ReadAsStringAsync());
        Assert.AreEqual("HIT", XCache(fr));
        Assert.AreEqual("lang=fr", await fr.Content.ReadAsStringAsync());
        Assert.AreEqual(2, f.Hits);
    }

    [TestMethod]
    [DynamicData(nameof(Modes))]
    [Timeout(60 * 1000)]
    public async Task Head_IsAnsweredFromACachedGet(PathMode mode)
    {
        using var f = new Fixture(mode, async context =>
        {
            context.Response.Headers.CacheControl = "max-age=60";
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("twelve bytes");
        });

        await (await f.GetAsync("/h")).Content.ReadAsStringAsync();
        var head = await f.HeadAsync("/h");
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
        Assert.AreEqual("HIT", XCache(head));
        Assert.AreEqual(12, head.Content.Headers.ContentLength);
        Assert.AreEqual(0, (await head.Content.ReadAsByteArrayAsync()).Length);
        Assert.AreEqual(1, f.Hits);
    }

    [TestMethod]
    [DynamicData(nameof(Modes))]
    [Timeout(60 * 1000)]
    public async Task StaleEntry_IsRevalidated_AndA304IsMerged(PathMode mode)
    {
        using var f = new Fixture(mode, async context =>
        {
            if (context.Request.Headers.IfNoneMatch.ToString() == "\"v1\"")
            {
                context.Response.StatusCode = 304;
                context.Response.Headers.ETag = "\"v1\"";
                context.Response.Headers.CacheControl = "max-age=300";
                context.Response.Headers["X-Revalidated"] = "yes";
                return;
            }

            // Already stale on arrival (Age beyond max-age) but revalidatable.
            context.Response.Headers.CacheControl = "max-age=10";
            context.Response.Headers.Age = "100";
            context.Response.Headers.ETag = "\"v1\"";
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("revalidated-body");
        });

        var first = await f.GetAsync("/r");
        Assert.AreEqual("revalidated-body", await first.Content.ReadAsStringAsync());

        var second = await f.GetAsync("/r");
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual("revalidated-body", await second.Content.ReadAsStringAsync());
        Assert.AreEqual("REVALIDATED", XCache(second));
        Assert.AreEqual("yes", second.Headers.GetValues("X-Revalidated").Single());
        Assert.AreEqual(1, f.ConditionalHits);

        var third = await f.GetAsync("/r");
        Assert.AreEqual("HIT", XCache(third), "the merged max-age=300 must apply");
        Assert.AreEqual("revalidated-body", await third.Content.ReadAsStringAsync());
        Assert.AreEqual(2, f.Hits);

        // The connection used for the 304 must still be good for the next request.
        var fresh = await f.GetAsync("/other");
        Assert.AreEqual(HttpStatusCode.OK, fresh.StatusCode);
    }

    [TestMethod]
    [DynamicData(nameof(Modes))]
    [Timeout(60 * 1000)]
    public async Task StaleIfError_AnswersAnOrigin500WithTheStaleCopy(PathMode mode)
    {
        var fail = 0;
        using var f = new Fixture(mode, async context =>
        {
            if (Volatile.Read(ref fail) == 1)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsync("origin is down");
                return;
            }

            context.Response.Headers.CacheControl = "max-age=10, stale-if-error=600";
            context.Response.Headers.Age = "100";
            await context.Response.WriteAsync("last-good");
        });

        Assert.AreEqual("last-good", await (await f.GetAsync("/e")).Content.ReadAsStringAsync());
        Volatile.Write(ref fail, 1);
        var served = await f.GetAsync("/e");
        Assert.AreEqual(HttpStatusCode.OK, served.StatusCode);
        Assert.AreEqual("last-good", await served.Content.ReadAsStringAsync());
        Assert.AreEqual("STALE", XCache(served));

        var other = await f.GetAsync("/never-cached");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, other.StatusCode);
        Assert.AreEqual("origin is down", await other.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DynamicData(nameof(Modes))]
    [Timeout(60 * 1000)]
    public async Task ClientNoCache_ReachesTheOriginConditionally(PathMode mode)
    {
        using var f = new Fixture(mode, async context =>
        {
            if (context.Request.Headers.IfNoneMatch.ToString() == "\"v1\"")
            {
                context.Response.StatusCode = 304;
                context.Response.Headers.CacheControl = "max-age=60";
                context.Response.Headers.ETag = "\"v1\"";
                return;
            }

            context.Response.Headers.CacheControl = "max-age=60";
            context.Response.Headers.ETag = "\"v1\"";
            await context.Response.WriteAsync("etagged");
        });

        await (await f.GetAsync("/n")).Content.ReadAsStringAsync();
        var forced = await f.GetAsync("/n", ("Cache-Control", "no-cache"));
        Assert.AreEqual("etagged", await forced.Content.ReadAsStringAsync());
        Assert.AreEqual(1, f.ConditionalHits);
        Assert.AreEqual(2, f.Hits);
    }

    [TestMethod]
    [DynamicData(nameof(Modes))]
    [Timeout(60 * 1000)]
    public async Task AuthorizationAndCookie_NeverTouchTheCache(PathMode mode)
    {
        using var f = new Fixture(mode, async context =>
        {
            context.Response.Headers.CacheControl = "max-age=60";
            await context.Response.WriteAsync("for:" + context.Request.Headers.Authorization.ToString());
        });

        Assert.AreEqual("for:", await (await f.GetAsync("/a")).Content.ReadAsStringAsync());
        var alice = await f.GetAsync("/a", ("Authorization", "Bearer alice"));
        Assert.AreEqual("for:Bearer alice", await alice.Content.ReadAsStringAsync());
        Assert.AreEqual(string.Empty, XCache(alice));
        var cookie = await f.GetAsync("/a", ("Cookie", "sid=1"));
        Assert.AreEqual(string.Empty, XCache(cookie));
        var anonymous = await f.GetAsync("/a");
        Assert.AreEqual("for:", await anonymous.Content.ReadAsStringAsync(), "an authorized response must never be handed to anyone else");
        Assert.AreEqual(3, f.Hits);
    }

    [TestMethod]
    [DynamicData(nameof(Modes))]
    [Timeout(60 * 1000)]
    public async Task Collapsing_ConcurrentMisses_ReachTheOriginOnce(PathMode mode)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = new Fixture(mode, async context =>
        {
            await release.Task;
            context.Response.Headers.CacheControl = "max-age=60";
            await context.Response.WriteAsync("collapsed");
        });

        var requests = Enumerable.Range(0, 20).Select(_ => Task.Run(() => f.GetAsync("/c"))).ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (f.Hits < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Task.Delay(500);
        release.SetResult();
        var responses = await Task.WhenAll(requests);
        foreach (var response in responses)
        {
            Assert.AreEqual("collapsed", await response.Content.ReadAsStringAsync());
        }

        Assert.AreEqual(1, f.Hits, "twenty concurrent misses must be one origin request");
    }
}
