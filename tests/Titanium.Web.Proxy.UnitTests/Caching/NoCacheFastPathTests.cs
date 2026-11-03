using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Caching;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Caching;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests.Caching;

/// <summary>
/// With no response-cache engine registered, the fast path never calls the cache: no state is attached,
/// the response filler does not run, and no cache metric is recorded.
/// </summary>
[TestClass]
public class NoCacheFastPathTests
{
    [TestMethod]
    public async Task NoCacheRegistered_FastPathHasNoCacheCalls()
    {
        var engine = new CountingEngine();
        using var proxy = new ProxyServer(false, false, false);
        var pass = new PassMiddleware();
        proxy.ReverseProxy = new Abstractions.ReverseProxyOptions
        {
            Middleware = [pass],
        };
        Assert.IsNull(proxy.HttpResponseCacheFiller);

        var find = typeof(ProxyServer).GetMethod("FindResponseCache", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsNull(find.Invoke(proxy, null), "a middleware list with no cache engine is not a cache");

        long cacheRequests = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Titanium.Web.Proxy" && instrument.Name == "twp.cache.requests")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => cacheRequests += value);
        listener.Start();

        var session = MakeSession(proxy);
        Assert.IsNull(session.ResponseCache);
        var fill = typeof(ProxyServer).GetMethod(
            "TryFillHttpResponseCacheAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)fill.Invoke(proxy, [session, CancellationToken.None])!;
        Assert.IsNull(session.ResponseCache);
        Assert.AreEqual(0, engine.Calls);
        Assert.AreEqual(0, cacheRequests);

        var ctx = new ProxyMiddlewareContext
        {
            Session = new object(),
            Request = new MiddlewareRequestView { Method = "GET", Path = "/", Host = "example.test" },
        };
        var pipeline = Middleware.ProxyMiddlewarePipeline.Build(
            proxy.ReverseProxy.Middleware!, static (_, _) => ValueTask.CompletedTask);
        await pipeline(ctx, CancellationToken.None);
        Assert.IsNull(CacheSessionSlot.StateOf(ctx));
        Assert.AreEqual(1, pass.Calls, "the non-cache middleware still ran");
        Assert.AreEqual(0, engine.Calls);
        Assert.AreEqual(0, cacheRequests);
        _ = engine;
    }

    private static SessionEventArgs MakeSession(ProxyServer proxy)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
    }

    private sealed class PassMiddleware : IProxyMiddleware
    {
        public int Calls { get; private set; }

        public ValueTask InvokeAsync(
            ProxyMiddlewareContext context,
            ProxyMiddlewareDelegate next,
            CancellationToken cancellationToken)
        {
            Calls++;
            return next(context, cancellationToken);
        }
    }

    private sealed class CountingEngine : IResponseCacheEngine
    {
        public int Calls { get; private set; }

        public int MaxEntryBytes => 1024;

        public ICacheBodyTee CreateBodyTee(long expectedLength)
        {
            Calls++;
            return new NullTee();
        }

        public OriginDecision OnOriginResponse(
            ICacheRequestState state,
            int statusCode,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            bool hasBody,
            long contentLength,
            bool chunked)
        {
            Calls++;
            return OriginDecision.Pass;
        }

        public void CompleteStore(ICacheRequestState state, CacheStorePlan plan, byte[]? body, long expectedLength) =>
            Calls++;

        public (string? IfNoneMatch, string? IfModifiedSince) ConditionalValidators(ICacheRequestState state)
        {
            Calls++;
            return default;
        }

        private sealed class NullTee : ICacheBodyTee
        {
            public void Append(byte[] source, int offset, int count)
            {
            }

            public byte[]? ToArray() => null;

            public void Dispose()
            {
            }
        }
    }
}
