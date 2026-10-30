using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Caching;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests.Caching;

/// <summary>
/// Drives the cache middleware the way the terminate-lite path does: request side through <c>InvokeAsync</c>, origin
/// side through <c>OnOriginResponse</c> / <c>CompleteStore</c>, so everything the handlers rely on is exercised
/// without sockets.
/// </summary>
internal sealed class CacheHarness
{
    private int _originHits;

    public CacheHarness(
        ResponseCacheOptions? options = null,
        MemoryHttpResponseCache? cache = null)
    {
        Cache = cache ?? new MemoryHttpResponseCache();
        Middleware = new HttpResponseCacheMiddleware(Cache, options: options);
    }

    public MemoryHttpResponseCache Cache { get; }

    public HttpResponseCacheMiddleware Middleware { get; }

    public int OriginHits => Volatile.Read(ref _originHits);

    /// <summary>The request headers the origin saw on its last fetch (including any conditional headers the cache added).</summary>
    public ConcurrentQueue<HeaderCollection> OriginRequests { get; } = new();

    public sealed record Reply(int Status, string[] Headers, string Body = "", bool Chunked = false)
    {
        public static Reply Ok(string body, params string[] headers) => new(200, headers, body);
    }

    public sealed record Result(
        bool FromCache, bool NextCalled, int Status, List<KeyValuePair<string, string>> Headers, byte[] Body)
    {
        public string Text => Encoding.UTF8.GetString(Body);

        public string? Header(string name) => Headers
            .Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault();
    }

    public async Task<Result> GetAsync(
        string path,
        Func<HeaderCollection, Task<Reply>> origin,
        string method = "GET",
        string host = "example.com",
        bool https = false,
        params string[] requestHeaders)
    {
        var lookup = requestHeaders.Select(Split).ToList();
        var ctx = new ProxyMiddlewareContext
        {
            Session = new object(),
            Request = new MiddlewareRequestView
            {
                Method = method,
                Path = path,
                Host = host,
                IsHttps = https,
                Authorization = lookup.FirstOrDefault(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).Value,
                GetHeaderValues = name =>
                {
                    var values = lookup.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                        .Select(h => h.Value).ToArray();
                    return values.Length == 0 ? null : values;
                },
            },
        };

        Result? fetched = null;
        var nextCalled = false;
        await Middleware.InvokeAsync(ctx, async (c, _) =>
        {
            nextCalled = true;
            var state = HttpResponseCacheMiddleware.StateOf(c);
            var outgoing = new HeaderCollection();
            foreach (var h in lookup)
            {
                outgoing.AddHeader(h.Key, h.Value);
            }

            if (state is not null)
            {
                HttpResponseCacheMiddleware.ApplyConditionalHeaders(state, outgoing);
            }

            Interlocked.Increment(ref _originHits);
            OriginRequests.Enqueue(outgoing);

            Reply reply;
            try
            {
                reply = await origin(outgoing).ConfigureAwait(false);
            }
            catch
            {
                state?.Abandon();
                throw;
            }

            var headers = reply.Headers.Select(Split).ToList();
            var body = Encoding.UTF8.GetBytes(reply.Body);
            if (state is null)
            {
                fetched = new Result(false, true, reply.Status, headers, body);
                return;
            }

            try
            {
                var decision = state.Owner.OnOriginResponse(state, reply.Status, headers, body.Length > 0 || reply.Chunked,
                    reply.Chunked ? -1 : body.Length, reply.Chunked);
                switch (decision.Action)
                {
                    case HttpResponseCacheMiddleware.OriginAction.Serve:
                        fetched = new Result(true, true, decision.Serve!.StatusCode, decision.Serve.Headers, decision.Serve.Body);
                        return;

                    case HttpResponseCacheMiddleware.OriginAction.StoreBuffered:
                    case HttpResponseCacheMiddleware.OriginAction.StoreStreamed:
                        state.Owner.CompleteStore(state, decision.Plan!, body, reply.Chunked ? -1 : body.Length);
                        break;
                }

                fetched = new Result(false, true, reply.Status, headers, body);
            }
            finally
            {
                state.Abandon();
            }
        }, CancellationToken.None).ConfigureAwait(false);

        if (ctx.IsHandled && fetched is null)
        {
            return new Result(true, nextCalled, ctx.HandledStatusCode ?? 0, ctx.HandledHeaders ?? [],
                ctx.HandledBodyBytes ?? Encoding.UTF8.GetBytes(ctx.HandledBody ?? string.Empty));
        }

        return fetched ?? throw new InvalidOperationException("the request neither reached the origin nor was answered");
    }

    public Task<Result> GetAsync(string path, Reply reply, params string[] requestHeaders) =>
        GetAsync(path, _ => Task.FromResult(reply), "GET", "example.com", false, requestHeaders);

    private static KeyValuePair<string, string> Split(string line)
    {
        var colon = line.IndexOf(':');
        return new KeyValuePair<string, string>(line[..colon].Trim(), line[(colon + 1)..].Trim());
    }
}

[TestClass]
public class HttpResponseCacheBehaviorTests
{
    private const string Fresh = "Cache-Control: max-age=60";

    [TestMethod]
    public async Task Get_MissThenHit_OriginSeenOnce()
    {
        var h = new CacheHarness();
        var first = await h.GetAsync("/a", CacheHarness.Reply.Ok("one", Fresh, "Content-Type: text/plain"));
        var second = await h.GetAsync("/a", CacheHarness.Reply.Ok("two", Fresh));

        Assert.IsFalse(first.FromCache);
        Assert.IsTrue(second.FromCache);
        Assert.AreEqual("one", second.Text);
        Assert.AreEqual(1, h.OriginHits);
        Assert.AreEqual("text/plain", second.Header("Content-Type"));
        Assert.AreEqual("3", second.Header("Content-Length"));
    }

    [TestMethod]
    public async Task Get_ResponseWithoutFreshness_IsNeverStored()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("one", "Content-Type: text/plain"));
        var second = await h.GetAsync("/a", CacheHarness.Reply.Ok("two", "Content-Type: text/plain"));
        Assert.IsFalse(second.FromCache);
        Assert.AreEqual("two", second.Text);
        Assert.AreEqual(0, h.Cache.Count);
    }

    [TestMethod]
    public async Task Get_SchemeIsPartOfTheKey()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", _ => Task.FromResult(CacheHarness.Reply.Ok("plain", Fresh)), "GET", "example.com", false);
        var https = await h.GetAsync("/a", _ => Task.FromResult(CacheHarness.Reply.Ok("secure", Fresh)), "GET", "example.com", true);
        Assert.IsFalse(https.FromCache);
        Assert.AreEqual("secure", https.Text);
    }

    [TestMethod]
    public async Task Get_HostIsCaseInsensitive_PathIsNot()
    {
        var h = new CacheHarness();
        await h.GetAsync("/Path", _ => Task.FromResult(CacheHarness.Reply.Ok("x", Fresh)), "GET", "Example.COM");
        Assert.IsTrue((await h.GetAsync("/Path", _ => Task.FromResult(CacheHarness.Reply.Ok("y", Fresh)), "GET", "example.com")).FromCache);
        Assert.IsFalse((await h.GetAsync("/path", _ => Task.FromResult(CacheHarness.Reply.Ok("y", Fresh)), "GET", "example.com")).FromCache);
    }

    [TestMethod]
    public async Task Head_IsAnsweredFromGet_WithEmptyBody_AndTheRepresentationLength()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("hello world", Fresh, "Content-Type: text/plain"));

        var head = await h.GetAsync("/a", _ => throw new AssertFailedException("origin must not be asked"), "HEAD");
        Assert.IsTrue(head.FromCache);
        Assert.AreEqual(200, head.Status);
        Assert.AreEqual(0, head.Body.Length);
        Assert.AreEqual("11", head.Header("Content-Length"));
        Assert.AreEqual(1, h.OriginHits);
    }

    [TestMethod]
    public async Task Head_Miss_IsNotStored()
    {
        var h = new CacheHarness();
        var head = await h.GetAsync("/a", _ => Task.FromResult(new CacheHarness.Reply(200, [Fresh], "")), "HEAD");
        Assert.IsFalse(head.FromCache);
        Assert.AreEqual(0, h.Cache.Count);
        var get = await h.GetAsync("/a", CacheHarness.Reply.Ok("body", Fresh));
        Assert.IsFalse(get.FromCache, "a HEAD answer must not satisfy a later GET");
    }

    [TestMethod]
    public async Task PostAndOtherMethods_AreNotCached()
    {
        var h = new CacheHarness();
        var post = await h.GetAsync("/a", _ => Task.FromResult(CacheHarness.Reply.Ok("x", Fresh)), "POST");
        Assert.IsFalse(post.FromCache);
        Assert.AreEqual(0, h.Cache.Count);
    }

    [TestMethod]
    public async Task Range_BypassesTheCache_BothWays()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("0123456789", Fresh));
        var ranged = await h.GetAsync("/a", CacheHarness.Reply.Ok("0123", Fresh), "Range: bytes=0-3");
        Assert.IsFalse(ranged.FromCache);
        Assert.AreEqual("0123", ranged.Text);

        var ifRange = await h.GetAsync("/a", CacheHarness.Reply.Ok("zz", Fresh), "If-Range: \"x\"");
        Assert.IsFalse(ifRange.FromCache);
        Assert.AreEqual(1, h.Cache.Count, "a ranged request must not overwrite the full entry");
    }

    [TestMethod]
    public async Task PartialContent_IsNeverStored()
    {
        var h = new CacheHarness();
        await h.GetAsync("/p", new CacheHarness.Reply(206, [Fresh, "Content-Range: bytes 0-3/10"], "0123"));
        Assert.AreEqual(0, h.Cache.Count);
    }

    [TestMethod]
    public async Task Authorization_IsIsolated_FromTheCache()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("public", Fresh));

        var auth = await h.GetAsync("/a", CacheHarness.Reply.Ok("private-for-alice", Fresh), "Authorization: Bearer alice");
        Assert.IsFalse(auth.FromCache, "an Authorization request must never be answered from the cache");
        Assert.AreEqual("private-for-alice", auth.Text);

        var anon = await h.GetAsync("/a", CacheHarness.Reply.Ok("other", Fresh));
        Assert.AreEqual("public", anon.Text, "an Authorization response must never be stored");

        var other = await h.GetAsync("/b", CacheHarness.Reply.Ok("only-authorized", Fresh), "Authorization: Bearer bob");
        Assert.IsFalse(other.FromCache);
        Assert.IsFalse((await h.GetAsync("/b", CacheHarness.Reply.Ok("anon-b", Fresh))).FromCache,
            "nothing about /b may have been stored by an Authorization request");
    }

    [TestMethod]
    public async Task Cookie_IsIsolated_FromTheCache()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("public", Fresh));
        var withCookie = await h.GetAsync("/a", CacheHarness.Reply.Ok("session", Fresh), "Cookie: sid=1");
        Assert.IsFalse(withCookie.FromCache);
        Assert.AreEqual("session", withCookie.Text);
        Assert.AreEqual("public", (await h.GetAsync("/a", CacheHarness.Reply.Ok("x", Fresh))).Text);
    }

    [TestMethod]
    public async Task SetCookieResponse_IsNeverStored()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("x", Fresh, "Set-Cookie: sid=1"));
        Assert.AreEqual(0, h.Cache.Count);
        var second = await h.GetAsync("/a", CacheHarness.Reply.Ok("y", Fresh, "Set-Cookie: sid=2"));
        Assert.AreEqual("y", second.Text);
    }

    [TestMethod]
    public async Task Vary_SplitsVariants_AndNeverMixesThem()
    {
        var h = new CacheHarness();
        CacheHarness.Reply En() => CacheHarness.Reply.Ok("hello", Fresh, "Vary: Accept-Language");
        CacheHarness.Reply Fr() => CacheHarness.Reply.Ok("bonjour", Fresh, "Vary: Accept-Language");

        var en1 = await h.GetAsync("/a", En(), "Accept-Language: en");
        var fr1 = await h.GetAsync("/a", Fr(), "Accept-Language: fr");
        var en2 = await h.GetAsync("/a", Fr(), "Accept-Language: en");
        var fr2 = await h.GetAsync("/a", En(), "Accept-Language: fr");
        var none = await h.GetAsync("/a", CacheHarness.Reply.Ok("neutral", Fresh, "Vary: Accept-Language"));

        Assert.IsFalse(en1.FromCache);
        Assert.IsFalse(fr1.FromCache);
        Assert.IsTrue(en2.FromCache);
        Assert.AreEqual("hello", en2.Text);
        Assert.IsTrue(fr2.FromCache);
        Assert.AreEqual("bonjour", fr2.Text);
        Assert.IsFalse(none.FromCache, "a request without the header is its own variant");
        Assert.AreEqual("neutral", none.Text);
        Assert.AreEqual(3, h.OriginHits);
    }

    [TestMethod]
    public async Task Vary_ValueNormalization_DoesNotSplitEquivalentRequests()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("x", Fresh, "Vary: Accept-Language"), "Accept-Language: EN");
        var again = await h.GetAsync("/a", CacheHarness.Reply.Ok("y", Fresh, "Vary: Accept-Language"), "Accept-Language:   en  ");
        Assert.IsTrue(again.FromCache);
    }

    [TestMethod]
    public async Task Poisoning_HeadersNotNamedByVary_DoNotChangeTheKey()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("clean", Fresh, "Vary: Accept-Language"), "Accept-Language: en");

        foreach (var evil in new[] { "X-Forwarded-Host: evil.test", "X-Original-URL: /admin", "User-Agent: x", "Host: evil.test" })
        {
            var hit = await h.GetAsync("/a", CacheHarness.Reply.Ok("evil", Fresh, "Vary: Accept-Language"), "Accept-Language: en", evil);
            Assert.IsTrue(hit.FromCache, evil);
            Assert.AreEqual("clean", hit.Text, evil);
        }

        Assert.AreEqual(1, h.OriginHits);
    }

    [TestMethod]
    public async Task Poisoning_ControlCharactersInKeyParts_BypassTheCache()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("clean", Fresh, "Vary: Accept-Language"), "Accept-Language: en");

        // A separator smuggled through a Vary header value must not be able to address another entry.
        var forged = await h.GetAsync("/a", CacheHarness.Reply.Ok("forged", Fresh, "Vary: Accept-Language"),
            "Accept-Language: en\u0001vary");
        Assert.IsFalse(forged.FromCache);
        Assert.AreEqual("forged", forged.Text);

        // Nor through the path or the Host.
        var path = await h.GetAsync("/a\u0001ae=\u0001x", CacheHarness.Reply.Ok("p", Fresh));
        Assert.IsFalse(path.FromCache);
        var host = await h.GetAsync("/a", _ => Task.FromResult(CacheHarness.Reply.Ok("h", Fresh)), "GET", "example.com\u0001");
        Assert.IsFalse(host.FromCache);

        Assert.AreEqual("clean", (await h.GetAsync("/a", CacheHarness.Reply.Ok("z", Fresh, "Vary: Accept-Language"), "Accept-Language: en")).Text);
    }

    [TestMethod]
    public async Task Poisoning_AcceptEncoding_SeparatesVariants()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("gz-bytes", Fresh, "Content-Encoding: gzip"), "Accept-Encoding: gzip");
        var br = await h.GetAsync("/a", CacheHarness.Reply.Ok("br-bytes", Fresh, "Content-Encoding: br"), "Accept-Encoding: br");
        Assert.IsFalse(br.FromCache);
        Assert.AreEqual("br", br.Header("Content-Encoding"));
        var gz = await h.GetAsync("/a", CacheHarness.Reply.Ok("never", Fresh), "Accept-Encoding: GZIP;q=1.0");
        Assert.IsTrue(gz.FromCache);
        Assert.AreEqual("gz-bytes", gz.Text);
        Assert.AreEqual("gzip", gz.Header("Content-Encoding"));
    }

    [TestMethod]
    public async Task Revalidation_ETag_304_MergesHeaders_AndServesStoredBody()
    {
        var h = new CacheHarness();
        // Age 100 against max-age 60: stored already stale, with a validator.
        await h.GetAsync("/a", CacheHarness.Reply.Ok("body", "Cache-Control: max-age=60", "Age: 100", "ETag: \"v1\"", "Content-Type: text/plain"));

        var revalidated = await h.GetAsync("/a", hdrs =>
        {
            Assert.AreEqual("\"v1\"", hdrs.GetFirstHeader("If-None-Match")?.Value, "the stored ETag must be sent back");
            return Task.FromResult(new CacheHarness.Reply(304, ["Cache-Control: max-age=300", "ETag: \"v1\"", "X-Added: yes"]));
        });

        Assert.IsTrue(revalidated.FromCache);
        Assert.AreEqual(200, revalidated.Status);
        Assert.AreEqual("body", revalidated.Text);
        Assert.AreEqual("yes", revalidated.Header("X-Added"));
        Assert.AreEqual("text/plain", revalidated.Header("Content-Type"));
        Assert.AreEqual("max-age=300", revalidated.Header("Cache-Control"));
        Assert.AreEqual("4", revalidated.Header("Content-Length"));

        var next = await h.GetAsync("/a", _ => throw new AssertFailedException("the merged freshness must apply"));
        Assert.IsTrue(next.FromCache);
        Assert.AreEqual("body", next.Text);
        Assert.AreEqual(2, h.OriginHits);
    }

    [TestMethod]
    public async Task Revalidation_LastModified_SendsIfModifiedSince()
    {
        var h = new CacheHarness();
        const string lastModified = "Sun, 31 Dec 2023 00:00:00 GMT";
        await h.GetAsync("/a", CacheHarness.Reply.Ok("body", "Cache-Control: max-age=10", "Age: 50", $"Last-Modified: {lastModified}"));

        var result = await h.GetAsync("/a", hdrs =>
        {
            Assert.AreEqual(lastModified, hdrs.GetFirstHeader("If-Modified-Since")?.Value);
            return Task.FromResult(new CacheHarness.Reply(304, ["Cache-Control: max-age=60"]));
        });
        Assert.IsTrue(result.FromCache);
        Assert.AreEqual("body", result.Text);
    }

    [TestMethod]
    public async Task Revalidation_200_ReplacesTheEntry()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("old", "Cache-Control: max-age=60", "Age: 100", "ETag: \"v1\""));
        var changed = await h.GetAsync("/a", CacheHarness.Reply.Ok("new", Fresh, "ETag: \"v2\""));
        Assert.IsFalse(changed.FromCache);
        Assert.AreEqual("new", changed.Text);
        var next = await h.GetAsync("/a", _ => throw new AssertFailedException("replaced entry is fresh"));
        Assert.AreEqual("new", next.Text);
    }

    [TestMethod]
    public async Task Revalidation_304_ThatTurnsUncacheable_AnswersOnceAndStoresNothing()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("body", "Cache-Control: max-age=60", "Age: 100", "ETag: \"v1\""));
        var once = await h.GetAsync("/a", new CacheHarness.Reply(304, ["Cache-Control: no-store"]));
        Assert.IsTrue(once.FromCache);
        Assert.AreEqual("body", once.Text);

        var next = await h.GetAsync("/a", CacheHarness.Reply.Ok("fresh-from-origin", "Cache-Control: no-store"));
        Assert.IsFalse(next.FromCache);
        Assert.AreEqual("fresh-from-origin", next.Text);
    }

    [TestMethod]
    public async Task ClientNoCache_And_MaxAgeZero_ForceRevalidation()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("body", Fresh, "ETag: \"v1\""));

        foreach (var directive in new[] { "Cache-Control: no-cache", "Cache-Control: max-age=0", "Pragma: no-cache" })
        {
            var before = h.OriginHits;
            var result = await h.GetAsync("/a", hdrs =>
            {
                Assert.IsNotNull(hdrs.GetFirstHeader("If-None-Match"), directive);
                return Task.FromResult(new CacheHarness.Reply(304, [Fresh, "ETag: \"v1\""]));
            }, "GET", "example.com", false, directive);
            Assert.AreEqual(before + 1, h.OriginHits, directive);
            Assert.AreEqual("body", result.Text, directive);
        }
    }

    [TestMethod]
    public async Task ClientNoStore_BypassesAndStoresNothing()
    {
        var h = new CacheHarness();
        var result = await h.GetAsync("/a", CacheHarness.Reply.Ok("x", Fresh), "Cache-Control: no-store");
        Assert.IsFalse(result.FromCache);
        Assert.AreEqual(0, h.Cache.Count);
    }

    [TestMethod]
    public async Task OnlyIfCached_MissAnswers504()
    {
        var h = new CacheHarness();
        var ctx = new ProxyMiddlewareContext
        {
            Session = new object(),
            Request = new MiddlewareRequestView
            {
                Method = "GET",
                Path = "/none",
                Host = "example.com",
                GetHeaderValues = name => name.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase) ? ["only-if-cached"] : null,
            },
        };
        var next = false;
        await h.Middleware.InvokeAsync(ctx, (_, _) => { next = true; return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.IsFalse(next);
        Assert.AreEqual(504, ctx.HandledStatusCode);
    }

    [TestMethod]
    public async Task ClientConditional_IsAnsweredWith304_FromAFreshEntry()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("body", Fresh, "ETag: \"v1\""));
        var notModified = await h.GetAsync("/a", _ => throw new AssertFailedException("answered from cache"),
            "GET", "example.com", false, "If-None-Match: \"v1\"");
        Assert.IsTrue(notModified.FromCache);
        Assert.AreEqual(304, notModified.Status);
        Assert.AreEqual(0, notModified.Body.Length);
        Assert.AreEqual("\"v1\"", notModified.Header("ETag"));
    }

    [TestMethod]
    public async Task ClientConditional_WithNoMatchingEntry_PassesThroughUntouched()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("body", "Cache-Control: max-age=60", "Age: 100", "ETag: \"v1\""));
        // Stale entry and the client has its own validator: the client's request goes through unmodified.
        var passthrough = await h.GetAsync("/a", hdrs =>
        {
            Assert.AreEqual("\"client\"", hdrs.GetFirstHeader("If-None-Match")?.Value);
            return Task.FromResult(new CacheHarness.Reply(304, [Fresh]));
        }, "GET", "example.com", false, "If-None-Match: \"client\"");
        Assert.AreEqual(304, passthrough.Status);
    }

    [TestMethod]
    public async Task StaleWhileRevalidate_ServesStale_OnlyOneRequestRevalidates()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("body", "Cache-Control: max-age=10, stale-while-revalidate=300", "Age: 20", "ETag: \"v1\""));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leader = h.GetAsync("/a", async hdrs =>
        {
            await gate.Task;
            Assert.IsNotNull(hdrs.GetFirstHeader("If-None-Match"));
            return new CacheHarness.Reply(304, [Fresh, "ETag: \"v1\""]);
        }, "GET", "example.com", false);
        while (h.OriginHits < 2)
        {
            await Task.Delay(5);
        }

        var stale = await h.GetAsync("/a", _ => throw new AssertFailedException("only the elected request may revalidate"));
        Assert.IsTrue(stale.FromCache);
        Assert.AreEqual("body", stale.Text);

        gate.SetResult();
        var revalidated = await leader;
        Assert.AreEqual("body", revalidated.Text);
        Assert.AreEqual(2, h.OriginHits);

        var fresh = await h.GetAsync("/a", _ => throw new AssertFailedException("freshened"));
        Assert.IsTrue(fresh.FromCache);
    }

    [TestMethod]
    public async Task StaleIfError_AnswersOriginFailureWithStale()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("body", "Cache-Control: max-age=10, stale-if-error=600", "Age: 20"));

        var served = await h.GetAsync("/a", new CacheHarness.Reply(503, [], "down"));
        Assert.IsTrue(served.FromCache);
        Assert.AreEqual(200, served.Status);
        Assert.AreEqual("body", served.Text);
    }

    [TestMethod]
    public async Task StaleIfError_Absent_PassesTheErrorThrough()
    {
        var h = new CacheHarness();
        await h.GetAsync("/a", CacheHarness.Reply.Ok("body", "Cache-Control: max-age=10", "Age: 20", "ETag: \"v\""));
        var error = await h.GetAsync("/a", new CacheHarness.Reply(503, [], "down"));
        Assert.IsFalse(error.FromCache);
        Assert.AreEqual(503, error.Status);
    }

    [TestMethod]
    public async Task ExposeHeaders_AddsAgeAndXCache_OnlyWhenEnabled()
    {
        var off = new CacheHarness();
        await off.GetAsync("/a", CacheHarness.Reply.Ok("x", Fresh));
        var plain = await off.GetAsync("/a", CacheHarness.Reply.Ok("x", Fresh));
        Assert.IsNull(plain.Header("X-Cache"));
        Assert.IsNull(plain.Header("Age"));

        var on = new CacheHarness(new ResponseCacheOptions { ExposeHeaders = true });
        await on.GetAsync("/a", CacheHarness.Reply.Ok("x", "Cache-Control: max-age=600", "Age: 7"));
        var hit = await on.GetAsync("/a", CacheHarness.Reply.Ok("x", Fresh));
        Assert.AreEqual("HIT", hit.Header("X-Cache"));
        Assert.IsTrue(long.Parse(hit.Header("Age")!) >= 7);
    }

    [TestMethod]
    public async Task ExposeHeaders_StaleAndRevalidated_AreLabelled()
    {
        var h = new CacheHarness(new ResponseCacheOptions { ExposeHeaders = true });
        await h.GetAsync("/a", CacheHarness.Reply.Ok("body", "Cache-Control: max-age=10, stale-while-revalidate=300", "Age: 20", "ETag: \"v\""));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leader = h.GetAsync("/a", async _ =>
        {
            await gate.Task;
            return new CacheHarness.Reply(304, [Fresh, "ETag: \"v\""]);
        }, "GET", "example.com", false);
        while (h.OriginHits < 2)
        {
            await Task.Delay(5);
        }

        var stale = await h.GetAsync("/a", _ => throw new AssertFailedException("stale"));
        Assert.AreEqual("STALE", stale.Header("X-Cache"));
        gate.SetResult();
        Assert.AreEqual("REVALIDATED", (await leader).Header("X-Cache"));
    }

    [TestMethod]
    public async Task Purge_ByUrlPrefix_RemovesAllVariantsAndNothingElse()
    {
        var h = new CacheHarness();
        await h.GetAsync("/assets/a.js", CacheHarness.Reply.Ok("a-en", Fresh, "Vary: Accept-Language"), "Accept-Language: en");
        await h.GetAsync("/assets/a.js", CacheHarness.Reply.Ok("a-fr", Fresh, "Vary: Accept-Language"), "Accept-Language: fr");
        await h.GetAsync("/assets/b.js", CacheHarness.Reply.Ok("b", Fresh));
        await h.GetAsync("/other", CacheHarness.Reply.Ok("o", Fresh));

        Assert.IsTrue(h.Cache.Purge("example.com/assets/a.js") >= 2);

        Assert.IsFalse((await h.GetAsync("/assets/a.js", CacheHarness.Reply.Ok("a2", Fresh, "Vary: Accept-Language"), "Accept-Language: en")).FromCache);
        Assert.IsFalse((await h.GetAsync("/assets/a.js", CacheHarness.Reply.Ok("a3", Fresh, "Vary: Accept-Language"), "Accept-Language: fr")).FromCache);
        Assert.IsTrue((await h.GetAsync("/assets/b.js", CacheHarness.Reply.Ok("x", Fresh))).FromCache);
        Assert.IsTrue((await h.GetAsync("/other", CacheHarness.Reply.Ok("x", Fresh))).FromCache);

        Assert.IsTrue(h.Cache.Purge("http://example.com/assets") >= 1);
        Assert.IsFalse((await h.GetAsync("/assets/b.js", CacheHarness.Reply.Ok("b2", Fresh))).FromCache);
        Assert.IsTrue((await h.GetAsync("/other", CacheHarness.Reply.Ok("x", Fresh))).FromCache);

        h.Cache.Purge();
        Assert.AreEqual(0, h.Cache.Count);
    }

    [TestMethod]
    public async Task ContentLengthMismatch_IsNeverStored()
    {
        var h = new CacheHarness();
        // The harness reports the true length, so drive CompleteStore directly with a short body.
        var ctx = new ProxyMiddlewareContext
        {
            Session = new object(),
            Request = new MiddlewareRequestView { Method = "GET", Path = "/short", Host = "example.com" },
        };
        await h.Middleware.InvokeAsync(ctx, (c, _) =>
        {
            var state = HttpResponseCacheMiddleware.StateOf(c)!;
            var decision = state.Owner.OnOriginResponse(state, 200, [new("Cache-Control", "max-age=60")], true, 100, false);
            state.Owner.CompleteStore(state, decision.Plan!, new byte[40], 100);
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        Assert.AreEqual(0, h.Cache.Count);
    }

    [TestMethod]
    public async Task OversizedBody_StreamsThrough_WithoutCaching()
    {
        var h = new CacheHarness(new ResponseCacheOptions { MaxEntryBytes = 16 });
        var big = new string('x', 64);
        var first = await h.GetAsync("/big", CacheHarness.Reply.Ok(big, Fresh));
        Assert.IsFalse(first.FromCache);
        Assert.AreEqual(0, h.Cache.Count);

        var small = await h.GetAsync("/small", CacheHarness.Reply.Ok("tiny", Fresh));
        Assert.IsFalse(small.FromCache);
        Assert.IsTrue((await h.GetAsync("/small", CacheHarness.Reply.Ok("x", Fresh))).FromCache);
    }

    [TestMethod]
    public async Task ChunkedBody_UnderTheLimit_IsStoredByTee()
    {
        var h = new CacheHarness();
        var ctx = new ProxyMiddlewareContext
        {
            Session = new object(),
            Request = new MiddlewareRequestView { Method = "GET", Path = "/chunked", Host = "example.com" },
        };
        await h.Middleware.InvokeAsync(ctx, (c, _) =>
        {
            var state = HttpResponseCacheMiddleware.StateOf(c)!;
            var decision = state.Owner.OnOriginResponse(state, 200, [new("Cache-Control", "max-age=60")], true, -1, true);
            Assert.AreEqual(HttpResponseCacheMiddleware.OriginAction.StoreStreamed, decision.Action);
            var slot = new CacheSessionSlot();
            slot.BeginTee(state, decision.Plan!, decision.ContentLength);
            slot.Tee!.Append(Encoding.UTF8.GetBytes("hello "), 0, 6);
            slot.Tee.Append(Encoding.UTF8.GetBytes("world"), 0, 5);
            slot.FinishStore();
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        var hit = await h.GetAsync("/chunked", _ => throw new AssertFailedException("stored from chunks"));
        Assert.AreEqual("hello world", hit.Text);
        Assert.AreEqual("11", hit.Header("Content-Length"));
        Assert.IsNull(hit.Header("Transfer-Encoding"));
    }

    [TestMethod]
    public void ResponseBodyTee_StopsAtTheLimit_AndReleasesItsBuffer()
    {
        using var tee = new ResponseBodyTee(10);
        tee.Append(new byte[6], 0, 6);
        Assert.IsFalse(tee.Overflowed);
        Assert.AreEqual(6, tee.ToArray()!.Length);
        tee.Append(new byte[6], 0, 6);
        Assert.IsTrue(tee.Overflowed);
        Assert.IsNull(tee.ToArray());
        tee.Append(new byte[1], 0, 1);
        Assert.IsNull(tee.ToArray());
    }

    [TestMethod]
    public async Task ThrowingOrigin_ReleasesTheFlight_AndWaitersFetchOnTheirOwn()
    {
        var h = new CacheHarness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leader = h.GetAsync("/a", async _ =>
        {
            await gate.Task;
            throw new InvalidOperationException("origin down");
        }, "GET", "example.com", false);
        while (h.OriginHits < 1)
        {
            await Task.Delay(5);
        }

        var waiter = h.GetAsync("/a", CacheHarness.Reply.Ok("recovered", Fresh));
        await Task.Delay(50);
        gate.SetResult();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await leader);
        var result = await waiter;
        Assert.AreEqual("recovered", result.Text);
        Assert.AreEqual(0, h.Middleware.InFlightFetches);
    }

    [TestMethod]
    public async Task Collapsing_200Concurrent_OneOriginFetch()
    {
        var h = new CacheHarness(new ResponseCacheOptions { CollapseMaxWaiters = 256, CollapseTimeout = TimeSpan.FromSeconds(30) });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CacheHarness.Reply> Origin(HeaderCollection _) => Task.Run(async () =>
        {
            await gate.Task;
            return CacheHarness.Reply.Ok("shared-body", Fresh, "Content-Type: text/plain");
        });

        var tasks = Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => h.GetAsync("/hot", Origin, "GET", "example.com", false)))
            .ToArray();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (h.OriginHits < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        await Task.Delay(300);
        Assert.AreEqual(1, h.OriginHits, "all other requests must be waiting behind the leader");
        gate.SetResult();

        var results = await Task.WhenAll(tasks);
        Assert.AreEqual(1, h.OriginHits, "200 concurrent misses must cost one origin fetch");
        Assert.IsTrue(results.All(r => r.Status == 200 && r.Text == "shared-body"));
        Assert.AreEqual(199, results.Count(r => r.FromCache));
        Assert.AreEqual(0, h.Middleware.InFlightFetches);
    }

    [TestMethod]
    public async Task Collapsing_WaitersAreBounded_OverflowFetchesIndependently()
    {
        var h = new CacheHarness(new ResponseCacheOptions { CollapseMaxWaiters = 2, CollapseTimeout = TimeSpan.FromSeconds(30) });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CacheHarness.Reply> Origin(HeaderCollection _) => Task.Run(async () =>
        {
            await gate.Task;
            return CacheHarness.Reply.Ok("body", Fresh);
        });

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => h.GetAsync("/hot", Origin, "GET", "example.com", false)))
            .ToArray();

        // 1 leader + 2 waiters; the other 7 are over the bound and go to the origin themselves.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (h.OriginHits < 8 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        await Task.Delay(200);
        Assert.AreEqual(8, h.OriginHits);
        gate.SetResult();
        var results = await Task.WhenAll(tasks);
        Assert.AreEqual(8, h.OriginHits);
        Assert.IsTrue(results.All(r => r.Text == "body"));
        Assert.AreEqual(2, results.Count(r => r.FromCache), "exactly the two bounded waiters are served from the leader's store");
    }

    [TestMethod]
    public async Task Collapsing_Timeout_FallsBackToIndependentFetches()
    {
        var h = new CacheHarness(new ResponseCacheOptions { CollapseTimeout = TimeSpan.FromMilliseconds(150) });
        var leaderGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leader = h.GetAsync("/slow", async _ =>
        {
            await leaderGate.Task;
            return CacheHarness.Reply.Ok("slow", Fresh);
        }, "GET", "example.com", false);
        while (h.OriginHits < 1)
        {
            await Task.Delay(5);
        }

        var followers = Enumerable.Range(0, 3)
            .Select(_ => h.GetAsync("/slow", CacheHarness.Reply.Ok("independent", Fresh)))
            .ToArray();
        var results = await Task.WhenAll(followers);

        Assert.IsTrue(results.All(r => r.Text == "independent"), "waiters must not be stuck behind a stuck leader");
        Assert.AreEqual(4, h.OriginHits);
        leaderGate.SetResult();
        await leader;
    }

    [TestMethod]
    public async Task Collapsing_DisabledByOption_EveryMissFetches()
    {
        var h = new CacheHarness(new ResponseCacheOptions { Collapse = false });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(() => h.GetAsync("/x", async _ =>
        {
            await gate.Task;
            return CacheHarness.Reply.Ok("b", Fresh);
        }, "GET", "example.com", false))).ToArray();
        while (h.OriginHits < 5)
        {
            await Task.Delay(5);
        }

        gate.SetResult();
        await Task.WhenAll(tasks);
        Assert.AreEqual(5, h.OriginHits);
    }

    [TestMethod]
    public async Task Metrics_CountResults_AndExposeGauges()
    {
        var counts = new ConcurrentDictionary<string, long>();
        long bytes = -1;
        long entries = -1;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Titanium.Web.Proxy" && instrument.Name.StartsWith("twp.cache.", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "twp.cache.requests")
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "result")
                    {
                        counts.AddOrUpdate((string)tag.Value!, value, (_, old) => old + value);
                    }
                }
            }
            else if (instrument.Name == "twp.cache.bytes")
            {
                bytes = value;
            }
            else if (instrument.Name == "twp.cache.entries")
            {
                entries = value;
            }
        });
        listener.Start();

        var h = new CacheHarness();
        await h.GetAsync("/m", CacheHarness.Reply.Ok("one", Fresh, "ETag: \"v\""));                          // miss
        await h.GetAsync("/m", CacheHarness.Reply.Ok("one", Fresh));                                           // hit
        await h.GetAsync("/m", CacheHarness.Reply.Ok("one", Fresh), "Authorization: Bearer x");                // bypass
        await h.GetAsync("/s", CacheHarness.Reply.Ok("s", "Cache-Control: max-age=10, stale-if-error=600", "Age: 20"));
        await h.GetAsync("/s", new CacheHarness.Reply(500, [], "down"));                                       // stale
        await h.GetAsync("/r", CacheHarness.Reply.Ok("r", "Cache-Control: max-age=10", "Age: 30", "ETag: \"r\""));
        await h.GetAsync("/r", new CacheHarness.Reply(304, [Fresh, "ETag: \"r\""]));                          // revalidated

        listener.RecordObservableInstruments();

        Assert.IsTrue(counts.GetValueOrDefault("miss") >= 3, "miss: " + counts.GetValueOrDefault("miss"));
        Assert.IsTrue(counts.GetValueOrDefault("hit") >= 1);
        Assert.IsTrue(counts.GetValueOrDefault("bypass") >= 1);
        Assert.IsTrue(counts.GetValueOrDefault("stale") >= 1);
        Assert.IsTrue(counts.GetValueOrDefault("revalidated") >= 1);
        Assert.IsTrue(entries >= h.Cache.Count, $"entries gauge {entries} < {h.Cache.Count}");
        Assert.IsTrue(bytes >= h.Cache.Bytes, $"bytes gauge {bytes} < {h.Cache.Bytes}");
        Assert.IsTrue(h.Cache.Bytes > 0);
    }

    [TestMethod]
    public void CacheMiddleware_KeepsTerminateLiteEligible_AndAddsNoInterception()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = 80,
            ForwardCleartext = true,
        };
        var get = new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
        Assert.IsTrue(proxy.CanUseH1TerminateLite(ep, get, false, false, false, null));

        proxy.ReverseProxy = new Titanium.Web.Proxy.Abstractions.ReverseProxyOptions
        {
            Middleware = [new HttpResponseCacheMiddleware(new MemoryHttpResponseCache())],
        };

        Assert.IsFalse(proxy.NeedsHttpInterception(),
            "the response cache must not subscribe BeforeResponse/AfterResponse: that would force every request onto the session path");
        Assert.IsTrue(proxy.CanUseH1TerminateLite(ep, get, false, false, false, null),
            "a cache hit is answered before the origin only on the lite path");
    }
}

[TestClass]
public class MemoryHttpResponseCacheBudgetTests
{
    private static CachedHttpResponse Entry(int bodyBytes, byte fill = 1) => new()
    {
        StatusCode = 200,
        Body = Enumerable.Repeat(fill, bodyBytes).ToArray(),
        Headers = [new KeyValuePair<string, string>("Content-Type", "application/octet-stream")],
        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        StoredUtc = DateTimeOffset.UtcNow,
    };

    [TestMethod]
    public void Defaults_Are64MiBAnd1MiB()
    {
        var cache = new MemoryHttpResponseCache();
        Assert.AreEqual(64L * 1024 * 1024, cache.MaxBytes);
        Assert.AreEqual(1024 * 1024, cache.MaxEntryBytes);
    }

    [TestMethod]
    public void Set_OverTheEntryLimit_IsRefused()
    {
        var cache = new MemoryHttpResponseCache(1024 * 1024, 4096);
        cache.Set("k", Entry(4097), TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, cache.Count);
        cache.Set("k", Entry(4096), TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, cache.Count);
    }

    [TestMethod]
    public void AccountingIncludesHeadersAndKey()
    {
        var cache = new MemoryHttpResponseCache(1024 * 1024, 8192);
        var bare = new CachedHttpResponse
        {
            StatusCode = 200,
            Body = new byte[100],
            Headers = [],
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        };
        cache.Set("a", bare, TimeSpan.FromMinutes(1));
        var withoutHeaders = cache.Bytes;
        cache.Purge();

        cache.Set("a", new CachedHttpResponse
        {
            StatusCode = 200,
            Body = new byte[100],
            Headers = Enumerable.Range(0, 20).Select(i => new KeyValuePair<string, string>($"X-Header-{i}", new string('v', 100))).ToList(),
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        }, TimeSpan.FromMinutes(1));
        Assert.IsTrue(cache.Bytes > withoutHeaders + 2000, "headers must be charged against the budget");
        Assert.IsTrue(withoutHeaders > 100, "the body is not the whole cost of an entry");
    }

    [TestMethod]
    public void Eviction_IsLeastRecentlyUsed_ByBytes()
    {
        // Small budget: one shard, room for four 8 KiB entries.
        var cache = new MemoryHttpResponseCache(40 * 1024, 8 * 1024);
        for (var i = 0; i < 4; i++)
        {
            cache.Set($"k{i}", Entry(8 * 1024), TimeSpan.FromMinutes(1));
        }

        Assert.AreEqual(4, cache.Count);
        Assert.IsTrue(cache.TryGet("k0", out _), "touch k0 so k1 is now the least recently used");
        cache.Set("k4", Entry(8 * 1024), TimeSpan.FromMinutes(1));

        Assert.IsTrue(cache.Bytes <= cache.MaxBytes);
        Assert.IsTrue(cache.Evictions >= 1);
        Assert.IsTrue(cache.TryGet("k0", out _), "the recently used entry survives");
        Assert.IsFalse(cache.TryGet("k1", out _), "the least recently used entry goes first");
        Assert.IsTrue(cache.TryGet("k4", out _));
    }

    [TestMethod]
    public void Replace_SameKey_DoesNotLeakBytes()
    {
        var cache = new MemoryHttpResponseCache(1024 * 1024, 8192);
        cache.Set("k", Entry(4000), TimeSpan.FromMinutes(1));
        var once = cache.Bytes;
        for (var i = 0; i < 50; i++)
        {
            cache.Set("k", Entry(4000), TimeSpan.FromMinutes(1));
        }

        Assert.AreEqual(1, cache.Count);
        Assert.AreEqual(once, cache.Bytes);
    }

    [TestMethod]
    public async Task Eviction_UnderConcurrency_StaysInBudget_AndNeverReturnsTornEntries()
    {
        const int maxBytes = 512 * 1024;
        var cache = new MemoryHttpResponseCache(maxBytes, 8 * 1024);
        var failures = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();

        var monitor = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var bytes = cache.Bytes;
                if (bytes > maxBytes)
                {
                    failures.Enqueue($"budget exceeded: {bytes} > {maxBytes}");
                }

                await Task.Delay(1);
            }
        });

        var workers = Enumerable.Range(0, 24).Select(worker => Task.Run(() =>
        {
            var random = new Random(worker);
            for (var i = 0; i < 4000; i++)
            {
                var id = random.Next(0, 600);
                var key = $"GET:http://example.com/{id}";
                var fill = (byte)(id % 251 + 1);
                if (random.Next(3) == 0 && cache.TryGet(key, out var hit))
                {
                    if (hit!.Body.Any(b => b != fill))
                    {
                        failures.Enqueue($"torn entry for {key}");
                    }
                }
                else
                {
                    cache.Set(key, Entry(random.Next(1, 8 * 1024), fill), TimeSpan.FromMinutes(5));
                }

                if (i % 500 == 0)
                {
                    cache.Purge($"example.com/{random.Next(0, 600)}");
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);
        stop.Cancel();
        await monitor;

        Assert.IsTrue(failures.IsEmpty, string.Join("; ", failures.Take(5)));
        Assert.IsTrue(cache.Bytes <= maxBytes);
        Assert.IsTrue(cache.Evictions > 0, "this workload must overflow the budget");
        Assert.IsTrue(cache.Count > 0);
    }

    [TestMethod]
    public void Purge_Prefix_MatchesKeyHostAndPathForms()
    {
        var cache = new MemoryHttpResponseCache();
        cache.Set("GET:https://example.com/a/1", Entry(10), TimeSpan.FromMinutes(1));
        cache.Set("GET:https://example.com/a/2", Entry(10), TimeSpan.FromMinutes(1));
        cache.Set("GET:https://example.com/b/1", Entry(10), TimeSpan.FromMinutes(1));

        Assert.AreEqual(2, cache.Purge("example.com/a/"));
        Assert.AreEqual(1, cache.Count);
        Assert.AreEqual(1, cache.Purge("GET:https://example.com/b"));
        Assert.AreEqual(0, cache.Count);
    }
}
