using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Caching;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests.Caching;

[TestClass]
public class MemoryHttpResponseCacheTests
{
    [TestMethod]
    public void Set_TryGet_Hit()
    {
        var cache = new MemoryHttpResponseCache();
        var body = Encoding.UTF8.GetBytes("hello");
        cache.Set("GET:host/a", new CachedHttpResponse
        {
            StatusCode = 200,
            Body = body,
            Headers = [new KeyValuePair<string, string>("Content-Type", "text/plain")],
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        }, TimeSpan.FromMinutes(5));

        Assert.AreEqual(1, cache.Count);
        Assert.IsTrue(cache.TryGet("GET:host/a", out var hit));
        Assert.IsNotNull(hit);
        Assert.AreEqual(200, hit!.StatusCode);
        CollectionAssert.AreEqual(body, hit.Body);
    }

    [TestMethod]
    public void TryGet_Miss()
    {
        var cache = new MemoryHttpResponseCache();
        Assert.IsFalse(cache.TryGet("missing", out var miss));
        Assert.IsNull(miss);
    }

    [TestMethod]
    public void TryGet_Expired_PurgesEntry()
    {
        var cache = new MemoryHttpResponseCache();
        cache.Set("GET:host/old", new CachedHttpResponse
        {
            StatusCode = 200,
            Body = [1],
            Headers = [],
            ExpiresUtc = DateTimeOffset.UtcNow.AddMilliseconds(-50),
        }, TimeSpan.FromMinutes(1));

        Assert.IsFalse(cache.TryGet("GET:host/old", out _));
        Assert.AreEqual(0, cache.Count);
    }

    [TestMethod]
    public void Purge_All_ClearsEntries()
    {
        var cache = new MemoryHttpResponseCache();
        cache.Set("a", Make(200), TimeSpan.FromMinutes(1));
        cache.Set("b", Make(200), TimeSpan.FromMinutes(1));
        Assert.AreEqual(2, cache.Purge());
        Assert.AreEqual(0, cache.Count);
    }

    [TestMethod]
    public void Purge_ByPrefix_RemovesMatchingKeys()
    {
        var cache = new MemoryHttpResponseCache();
        cache.Set("GET:api/v1", Make(200), TimeSpan.FromMinutes(1));
        cache.Set("GET:api/v2", Make(200), TimeSpan.FromMinutes(1));
        cache.Set("GET:other", Make(200), TimeSpan.FromMinutes(1));

        Assert.AreEqual(2, cache.Purge("api"));
        Assert.AreEqual(1, cache.Count);
        Assert.IsTrue(cache.TryGet("GET:other", out _));
    }

    [TestMethod]
    public void Set_UsesDefaultTtl_WhenExpiresUnset()
    {
        var cache = new MemoryHttpResponseCache();
        cache.Set("k", new CachedHttpResponse
        {
            StatusCode = 200,
            Body = [9],
            Headers = [],
            ExpiresUtc = DateTimeOffset.MinValue,
        }, TimeSpan.FromMinutes(2));

        Assert.IsTrue(cache.TryGet("k", out var hit));
        Assert.IsTrue(hit!.ExpiresUtc > DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public void TryGet_EmptyKey_Throws()
    {
        var cache = new MemoryHttpResponseCache();
        Assert.ThrowsExactly<ArgumentException>(() => cache.TryGet("", out _));
    }

    private static CachedHttpResponse Make(int status) => new()
    {
        StatusCode = status,
        Body = [],
        Headers = [],
        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5),
    };
}

[TestClass]
public class HttpResponseCacheMiddlewareTests
{
    [TestMethod]
    public async Task InvokeAsync_NonSession_PassesThrough()
    {
        var mw = new HttpResponseCacheMiddleware(new MemoryHttpResponseCache());
        var nextCalled = false;
        var ctx = new ProxyMiddlewareContext { Session = new object() };
        await mw.InvokeAsync(ctx, (_, _) =>
        {
            nextCalled = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        Assert.IsTrue(nextCalled);
        Assert.IsFalse(ctx.IsHandled);
    }

    [TestMethod]
    public async Task InvokeAsync_GetMiss_CallsNext()
    {
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy, "GET", "http://example.com/miss");
        var mw = new HttpResponseCacheMiddleware(new MemoryHttpResponseCache());
        var nextCalled = false;
        var ctx = new ProxyMiddlewareContext { Session = session };
        await mw.InvokeAsync(ctx, (_, _) =>
        {
            nextCalled = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        Assert.IsTrue(nextCalled);
        Assert.IsFalse(ctx.IsHandled);
    }

    [TestMethod]
    public async Task InvokeAsync_GetHit_ShortCircuits()
    {
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy, "GET", "http://example.com/hit");
        var cache = new MemoryHttpResponseCache();
        cache.Set("GET:example.com/hit", new CachedHttpResponse
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes("cached"),
            Headers = [new KeyValuePair<string, string>("X-From", "cache")],
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        }, TimeSpan.FromMinutes(5));

        var mw = new HttpResponseCacheMiddleware(cache);
        var nextCalled = false;
        var ctx = new ProxyMiddlewareContext { Session = session };
        await mw.InvokeAsync(ctx, (_, _) =>
        {
            nextCalled = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        Assert.IsFalse(nextCalled);
        Assert.IsTrue(ctx.IsHandled);
    }

    [TestMethod]
    public void TryCacheCurrentResponse_StoresGet200Body()
    {
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy, "GET", "http://example.com/store");
        session.HttpClient.Response.StatusCode = 200;
        session.HttpClient.Response.Body = Encoding.UTF8.GetBytes("ok");
        session.HttpClient.Response.IsBodyRead = true;
        session.HttpClient.Response.Headers.AddHeader("Content-Type", "text/plain");

        var cache = new MemoryHttpResponseCache();
        var mw = new HttpResponseCacheMiddleware(cache, TimeSpan.FromMinutes(3));
        mw.TryCacheCurrentResponse(session);

        Assert.IsTrue(cache.TryGet("GET:example.com/store", out var hit));
        Assert.AreEqual(200, hit!.StatusCode);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("ok"), hit.Body);
    }

    [TestMethod]
    public void TryCacheCurrentResponse_SkipsNonGet()
    {
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy, "POST", "http://example.com/x");
        session.HttpClient.Response.StatusCode = 200;
        session.HttpClient.Response.Body = [1];
        session.HttpClient.Response.IsBodyRead = true;

        var cache = new MemoryHttpResponseCache();
        new HttpResponseCacheMiddleware(cache).TryCacheCurrentResponse(session);
        Assert.AreEqual(0, cache.Count);
    }

    [TestMethod]
    public void Ctor_NullCache_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new HttpResponseCacheMiddleware(null!));
    }

    private static SessionEventArgs MakeSession(ProxyServer proxy, string method, string url)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Request.Method = method;
        session.HttpClient.Request.RequestUriString = url;
        session.HttpClient.Request.Host = new Uri(url).Host;
        return session;
    }
}
