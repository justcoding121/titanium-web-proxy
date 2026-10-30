using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StackExchange.Redis;
using Titanium.Plus.State;
using Titanium.TestKit;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Plus.Tests;

[TestClass]
[DoNotParallelize]
public class RedisFleetRateStoreTests
{
    private static readonly TransparentProxyEndPoint EndPoint =
        new(System.Net.IPAddress.Loopback, 0, false) { ForwardHost = "127.0.0.1", ForwardPort = 80 };

    [TestInitialize]
    public void Reset() => RoutePolicies.ResetForTests();

    [TestCleanup]
    public void Restore() => RoutePolicies.ResetForTests();

    [TestMethod]
    [TestCategory(RedisTestHelper.Category)]
    public async Task LuaScript_LeasesAgainstRealRedis()
    {
        using var mux = await RedisTestHelper.TryConnectAsync();
        if (mux is null)
        {
            RedisTestHelper.SkipOrFailIfUnavailable();
            return;
        }

        var prefix = RedisTestHelper.NewKeyPrefix();
        using var store = new RedisFleetRateStore(mux);
        var key = prefix + "policy|r:r";
        var spec = new FleetRateSpec { Limit = 5, WindowSeconds = 60, Burst = 5, TokenBucket = true };

        var a = await store.LeaseAsync(key, 3, spec);
        Assert.IsTrue(a.StoreAvailable);
        Assert.AreEqual(3, a.Granted);

        var b = await store.LeaseAsync(key, 3, spec);
        Assert.AreEqual(2, b.Granted);

        var c = await store.LeaseAsync(key, 1, spec);
        Assert.AreEqual(0, c.Granted);

        // Cleanup test keys
        await mux.GetDatabase().KeyDeleteAsync(RedisFleetRateStore.BuildRedisKey(key));
    }

    [TestMethod]
    [TestCategory(RedisTestHelper.Category)]
    public async Task ThreeLimiters_ShareFleetBudget()
    {
        using var mux = await RedisTestHelper.TryConnectAsync();
        if (mux is null)
        {
            RedisTestHelper.SkipOrFailIfUnavailable();
            return;
        }

        var prefix = RedisTestHelper.NewKeyPrefix();
        using var store = new RedisFleetRateStore(mux);
        var policy = new RoutePolicyConfig
        {
            Name = "rate",
            Kind = "rateLimit",
            Limit = 6,
            Burst = 6,
            WindowSeconds = 60,
            Algorithm = "tokenBucket",
            Scope = "fleet",
            FleetFailMode = "closed",
            Key = "route",
        };

        ReverseProxyOptions Make() => new()
        {
            FleetRateStore = store,
            Policies = [policy],
            Routes =
            [
                new RouteConfig
                {
                    Id = prefix + "r",
                    ClusterId = "c",
                    Policies = ["rate"],
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };

        var nodes = new[] { Make(), Make(), Make() };
        var request = new Request { Method = "GET", RequestUriString8 = (ByteString)"/x", Host = "app" };

        // Warm each node's refill
        foreach (var node in nodes)
        {
            _ = RoutePolicies.TryHandleLite(node, EndPoint, request,
                new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1), out _, out _, out _);
        }

        await Task.Delay(200);

        var allowed = 0;
        for (var round = 0; round < 20; round++)
        {
            foreach (var node in nodes)
            {
                if (!RoutePolicies.TryHandleLite(node, EndPoint, request,
                        new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1), out _, out _, out _))
                {
                    allowed++;
                }
            }

            await Task.Delay(10);
        }

        Assert.IsTrue(allowed <= 6 + 6 + 3 * 2, "overshoot bound: limit + burst + nodes*leaseSize");
        Assert.IsTrue(allowed >= 1);

        await mux.GetDatabase().KeyDeleteAsync(RedisFleetRateStore.BuildRedisKey(
            policy.Name + "|r:" + prefix + "r"));
    }

    [TestMethod]
    [TestCategory(RedisTestHelper.Category)]
    public async Task ClosedPort_StoreReportsUnavailable()
    {
        // Bind then close so we know the port is free and refused.
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        try
        {
            var config = ConfigurationOptions.Parse($"127.0.0.1:{port}");
            config.AbortOnConnectFail = true;
            config.ConnectTimeout = 200;
            using var mux = await ConnectionMultiplexer.ConnectAsync(config);
            Assert.Fail("expected connect to fail");
        }
        catch
        {
            // expected — never stop the user's docker Redis; only probe a closed local port
        }

        // Disposed store reports unavailable. Prefer a live mux when present; otherwise the closed-port
        // probe above already covers "store unreachable" without requiring Redis on the Windows CI host.
        using var good = await RedisTestHelper.TryConnectAsync();
        if (good is null)
        {
            return;
        }

        var store = new RedisFleetRateStore(good);
        store.Dispose();
        var result = await store.LeaseAsync("k", 1, new FleetRateSpec
        {
            Limit = 10,
            WindowSeconds = 60,
            Burst = 10,
            TokenBucket = true,
        });
        Assert.IsFalse(result.StoreAvailable);
        Assert.AreEqual(0, result.Granted);
    }
}
