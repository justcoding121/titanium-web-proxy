using Microsoft.VisualStudio.TestTools.UnitTesting;
using StackExchange.Redis;
using Titanium.Plus.State;
using Titanium.Plus.Tests.Support;
using Titanium.TestKit;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Tests;

/// <summary>
///     Windows-Sonar-visible RedisFleetRateStore coverage without TestCategory=Redis.
///     Uses an in-process multiplexer stub for the Lua grant path, plus FakeResp for TryStart connect refusal.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RedisFleetRateStoreFakeRespTests
{
    [TestMethod]
    [Timeout(15_000)]
    public async Task LeaseAsync_AgainstInProcessMux_GrantsTokens()
    {
        // Bug class: ScriptEvaluate success path never runs on the Windows coverage merge.
        var mux = FakeRedisMultiplexer.Create(out var evalCount, values =>
        {
            // ARGV[0] is want; echo it back as the grant.
            var want = values.Length > 0 && values[0].TryParse(out int n) ? n : 0;
            return RedisResult.Create((RedisValue)want);
        });
        var before = RedisFleetRateStore.LiveStores;
        using (var store = new RedisFleetRateStore(mux))
        {
            Assert.AreEqual(before + 1, RedisFleetRateStore.LiveStores);
            var result = await store.LeaseAsync("policy|r:route", 3, new FleetRateSpec
            {
                Limit = 10,
                WindowSeconds = 60,
                Burst = 10,
                TokenBucket = true,
            });
            Assert.IsTrue(result.StoreAvailable);
            Assert.AreEqual(3, result.Granted);
            Assert.IsTrue(evalCount() >= 1);
        }

        Assert.AreEqual(before, RedisFleetRateStore.LiveStores);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task LeaseAsync_WhenScriptThrows_FailsClosed()
    {
        // Bug class: a Redis EVAL exception escapes and aborts the request instead of fail-closed.
        var mux = FakeRedisMultiplexer.Create(out _, _ => throw new RedisServerException("boom"));
        using var store = new RedisFleetRateStore(mux);
        var result = await store.LeaseAsync("k", 1, new FleetRateSpec
        {
            Limit = 1,
            WindowSeconds = 1,
            Burst = 1,
            TokenBucket = false,
        });
        Assert.IsFalse(result.StoreAvailable);
        Assert.AreEqual(0, result.Granted);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task TryStart_AgainstFakeRespThatNeverHandshakes_ReturnsNullWithoutThrowing()
    {
        // Bug class: a listener that accepts TCP but never speaks Redis must not throw during Plus activation.
        await using var redis = new FakeRespServer { CloseAfterNextCommand = true };
        var before = RedisFleetRateStore.LiveStores;
        var context = new PlusActivationContext { ProxyServer = new object() };
        var store = RedisFleetRateStore.TryStart(context, new Dictionary<string, string>
        {
            ["state.redis"] = redis.Endpoint,
            ["state.fleetRate"] = "true",
        });
        Assert.IsNull(store);
        Assert.AreEqual(before, RedisFleetRateStore.LiveStores);
        Assert.IsNull(context.FleetRateStore);
    }
}
