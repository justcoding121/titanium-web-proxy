#pragma warning disable TWP001
using System.Collections.Generic;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Caching;
using Titanium.Web.Proxy.Caching;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <c>ApplyOriginCacheDecision</c> / <c>ApplyStoreOriginCacheDecision</c> store and tee branches.
/// </summary>
[TestClass]
public class ResponseHandlerOriginCacheWave43FatGapTests
{
    private static readonly MethodInfo ApplyOrigin = typeof(ProxyServer).GetMethod(
        "ApplyOriginCacheDecision", Wave42FatGapTestSupport.PrivateStatic)!;
    private static readonly MethodInfo ApplyStore = typeof(ProxyServer).GetMethod(
        "ApplyStoreOriginCacheDecision", Wave42FatGapTestSupport.PrivateStatic)!;

    [TestMethod]
    public void ApplyStoreOriginCache_BodyRead_WireOrPlain_CompletesOrAbandons()
    {
        var engine = new RecordingCacheEngine();
        var state = new StubCacheState(engine);
        var slot = new CacheSessionSlot();
        var plan = new StubPlan();

        var plain = new Response { StatusCode = 200, IsBodyRead = true, Body = [1, 2], ContentLength = 2 };
        ApplyStore.Invoke(null, [slot, state, plain,
            new OriginDecision { Action = OriginAction.StoreBuffered, Plan = plan }]);
        Assert.AreEqual(1, engine.CompleteCalls);
        Assert.AreEqual(0, state.AbandonCalls);

        engine.CompleteCalls = 0;
        var encoded = new Response
        {
            StatusCode = 200,
            IsBodyRead = true,
            Body = [9],
            ContentLength = 1,
            BodyIsWireEncoded = false
        };
        encoded.Headers.AddHeader("Content-Encoding", "gzip");
        ApplyStore.Invoke(null, [slot, state, encoded,
            new OriginDecision { Action = OriginAction.StoreBuffered, Plan = plan }]);
        Assert.AreEqual(0, engine.CompleteCalls);
        Assert.AreEqual(1, state.AbandonCalls);
    }

    [TestMethod]
    public void ApplyStoreOriginCache_NoBody_CompletesEmpty()
    {
        var engine = new RecordingCacheEngine();
        var state = new StubCacheState(engine);
        var response = new Response { StatusCode = 204, ContentLength = 0 };
        ApplyStore.Invoke(null, [new CacheSessionSlot(), state, response,
            new OriginDecision { Action = OriginAction.StoreBuffered, Plan = new StubPlan() }]);
        Assert.AreEqual(1, engine.CompleteCalls);
        CollectionAssert.AreEqual(System.Array.Empty<byte>(), engine.LastBody!);
    }

    [TestMethod]
    public void ApplyStoreOriginCache_StreamingBody_BeginsTee()
    {
        var engine = new RecordingCacheEngine();
        var state = new StubCacheState(engine);
        var slot = new CacheSessionSlot();
        var response = new Response { StatusCode = 200, ContentLength = 100 };
        ApplyStore.Invoke(null, [slot, state, response,
            new OriginDecision
            {
                Action = OriginAction.StoreStreamed,
                Plan = new StubPlan(),
                ContentLength = 100
            }]);
        Assert.IsNotNull(slot.Tee);
    }

    [TestMethod]
    public void ApplyOriginCache_ServeAction_InvokesRespondFromCache()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = Wave42FatGapTestSupport.MakeSession(proxy);
        var slot = new CacheSessionSlot();
        var engine = new RecordingCacheEngine();
        var state = new StubCacheState(engine);
        var response = new Response { StatusCode = 200, ContentLength = 0 };
        var serve = new CachedServe
        {
            StatusCode = 304,
            Headers = [new KeyValuePair<string, string>("etag", "\"x\"")],
            Body = []
        };
        ApplyOrigin.Invoke(null,
        [
            session, slot, state, response,
            new OriginDecision { Action = OriginAction.Serve, Serve = serve }
        ]);
        Assert.AreEqual(304, session.HttpClient.Response.StatusCode);
    }

    private sealed class StubPlan : CacheStorePlan;

    private sealed class StubCacheState(RecordingCacheEngine owner) : ICacheRequestState
    {
        public int AbandonCalls;
        public IResponseCacheEngine Owner => owner;
        public void Abandon() => AbandonCalls++;
    }

    private sealed class RecordingCacheEngine : IResponseCacheEngine
    {
        public int CompleteCalls;
        public byte[]? LastBody;
        public int MaxEntryBytes => 1_000_000;

        public ICacheBodyTee CreateBodyTee(long expectedLength) => new NullTee();

        public OriginDecision OnOriginResponse(ICacheRequestState state, int statusCode,
            IReadOnlyList<KeyValuePair<string, string>> headers, bool hasBody, long contentLength, bool chunked) =>
            OriginDecision.Pass;

        public void CompleteStore(ICacheRequestState state, CacheStorePlan plan, byte[]? body, long expectedLength)
        {
            CompleteCalls++;
            LastBody = body;
        }

        public (string? IfNoneMatch, string? IfModifiedSince) ConditionalValidators(ICacheRequestState state) =>
            default;

        private sealed class NullTee : ICacheBodyTee
        {
            public void Append(byte[] source, int offset, int count) { }
            public byte[]? ToArray() => null;
            public void Dispose() { }
        }
    }
}
#pragma warning restore TWP001
