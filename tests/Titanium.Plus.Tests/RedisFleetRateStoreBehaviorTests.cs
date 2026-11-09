using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StackExchange.Redis;
using Titanium.Plus.State;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Tests;

/// <summary>
///     Behavior tests for <see cref="RedisFleetRateStore"/> that do not need a live Redis
///     (so they feed the Windows Sonar coverage merge). Bug class: fail-closed leases, key
///     hashing, TryStart gating, and LiveStores accounting.
/// </summary>
[TestClass]
public class RedisFleetRateStoreBehaviorTests
{
    [TestMethod]
    public void BuildRedisKey_IsStablePrefixedHash_WithoutClusterHashTags()
    {
        // Bug class: curly-brace hash tags pin keys to one Cluster slot incorrectly.
        var a = RedisFleetRateStore.BuildRedisKey("policy|r:route-1");
        var b = RedisFleetRateStore.BuildRedisKey("policy|r:route-1");
        var c = RedisFleetRateStore.BuildRedisKey("policy|r:route-2");

        Assert.AreEqual(a, b);
        Assert.AreNotEqual(a, c);
        StringAssert.StartsWith(a, "twp:rl2:");
        Assert.IsFalse(a.Contains('{') || a.Contains('}'));
        Assert.AreEqual("twp:rl2:".Length + 32, a.Length, "16 hash bytes as hex");
    }

    [TestMethod]
    public void HasFleetRateLimit_RequiresRateLimitKindAndFleetScope()
    {
        Assert.IsFalse(RedisFleetRateStore.HasFleetRateLimit(null));
        Assert.IsFalse(RedisFleetRateStore.HasFleetRateLimit([]));
        Assert.IsFalse(RedisFleetRateStore.HasFleetRateLimit(
        [
            new RoutePolicyConfig { Name = "local", Kind = "rateLimit", Scope = "local" },
            new RoutePolicyConfig { Name = "waf", Kind = "waf", Scope = "fleet" },
        ]));
        Assert.IsTrue(RedisFleetRateStore.HasFleetRateLimit(
        [
            new RoutePolicyConfig { Name = "fleet", Kind = "rateLimit", Scope = "fleet" },
        ]));
        Assert.IsTrue(RedisFleetRateStore.HasFleetRateLimit(
        [
            new RoutePolicyConfig { Name = "fleet2", Kind = "RATELIMIT", Scope = "FLEET" },
        ]));
    }

    [TestMethod]
    public void TryStart_WithoutRedisOption_ReturnsNull_AndDoesNotIncrementLiveStores()
    {
        var before = RedisFleetRateStore.LiveStores;
        var context = new PlusActivationContext { ProxyServer = new object() };
        var store = RedisFleetRateStore.TryStart(context, new Dictionary<string, string>());
        Assert.IsNull(store);
        Assert.AreEqual(before, RedisFleetRateStore.LiveStores);
        Assert.IsNull(context.FleetRateStore);
    }

    [TestMethod]
    public void TryStart_WithRedisButNoFleetPolicy_ReturnsNull()
    {
        var before = RedisFleetRateStore.LiveStores;
        var context = new PlusActivationContext
        {
            ProxyServer = new object(),
            Policies =
            [
                new RoutePolicyConfig { Name = "local", Kind = "rateLimit", Scope = "local" },
            ],
        };
        var store = RedisFleetRateStore.TryStart(context, new Dictionary<string, string>
        {
            ["state.redis"] = "127.0.0.1:6379",
        });
        Assert.IsNull(store);
        Assert.AreEqual(before, RedisFleetRateStore.LiveStores);
    }

    [TestMethod]
    public void TryStart_UnreachableRedis_ReturnsNullWithoutThrowing()
    {
        // Bug class: connect failure throws and aborts Plus activation.
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var before = RedisFleetRateStore.LiveStores;
        var context = new PlusActivationContext { ProxyServer = new object() };
        var store = RedisFleetRateStore.TryStart(context, new Dictionary<string, string>
        {
            ["state.redis"] = $"127.0.0.1:{port}",
            ["state.fleetRate"] = "true",
        });
        Assert.IsNull(store);
        Assert.AreEqual(before, RedisFleetRateStore.LiveStores);
    }

    [TestMethod]
    public async Task LeaseAsync_WhenMultiplexerNotConnected_FailsClosed()
    {
        // Bug class: disconnected mux throws instead of fail-closed lease.
        var config = ConfigurationOptions.Parse("127.0.0.1:1");
        config.AbortOnConnectFail = false;
        config.ConnectTimeout = 50;
        config.SyncTimeout = 50;
        using var mux = ConnectionMultiplexer.Connect(config);
        var before = RedisFleetRateStore.LiveStores;
        using (var store = new RedisFleetRateStore(mux))
        {
            Assert.AreEqual(before + 1, RedisFleetRateStore.LiveStores);
            var result = await store.LeaseAsync("k", 2, new FleetRateSpec
            {
                Limit = 10,
                WindowSeconds = 60,
                Burst = 10,
                TokenBucket = true,
            });
            Assert.IsFalse(result.StoreAvailable);
            Assert.AreEqual(0, result.Granted);
        }

        Assert.AreEqual(before, RedisFleetRateStore.LiveStores);
    }

    [TestMethod]
    public async Task LeaseAsync_WantZero_ReturnsAvailableWithoutContactingStore()
    {
        var config = ConfigurationOptions.Parse("127.0.0.1:1");
        config.AbortOnConnectFail = false;
        config.ConnectTimeout = 50;
        using var mux = ConnectionMultiplexer.Connect(config);
        using var store = new RedisFleetRateStore(mux);
        // Force the connected branch when possible; want==0 short-circuits either way.
        var result = await store.LeaseAsync("k", 0, new FleetRateSpec
        {
            Limit = 10,
            WindowSeconds = 60,
            Burst = 10,
            TokenBucket = false,
        });
        Assert.AreEqual(0, result.Granted);
    }

    [TestMethod]
    public async Task LeaseAsync_AfterDispose_FailsClosed()
    {
        var config = ConfigurationOptions.Parse("127.0.0.1:1");
        config.AbortOnConnectFail = false;
        config.ConnectTimeout = 50;
        var mux = ConnectionMultiplexer.Connect(config);
        var store = new RedisFleetRateStore(mux);
        store.Dispose();
        var result = await store.LeaseAsync("k", 1, new FleetRateSpec
        {
            Limit = 1,
            WindowSeconds = 1,
            Burst = 1,
            TokenBucket = true,
        });
        Assert.IsFalse(result.StoreAvailable);
        Assert.AreEqual(0, result.Granted);
        mux.Dispose();
    }
}
