using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.TestKit;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
[DoNotParallelize]
public class FleetRateLimitTests
{
    private static readonly TransparentProxyEndPoint EndPoint =
        new(IPAddress.Loopback, 0, false) { ForwardHost = "127.0.0.1", ForwardPort = 80 };

    [TestInitialize]
    public void Reset() => RoutePolicies.ResetForTests();

    [TestCleanup]
    public void Restore() => RoutePolicies.ResetForTests();

    private static ReverseProxyOptions Options(RoutePolicyConfig policy, IFleetRateStore? store = null) => new()
    {
        FleetRateStore = store,
        Policies = [policy],
        Routes =
        [
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c",
                Policies = [policy.Name],
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        ],
    };

    private static RoutePolicyConfig Fleet(
        int limit = 10,
        string failMode = "local",
        double? localShare = null,
        string algorithm = "tokenBucket",
        int? burst = null) => new()
    {
        Name = "rate",
        Kind = "rateLimit",
        Limit = limit,
        WindowSeconds = 60,
        Burst = burst ?? limit,
        Algorithm = algorithm,
        Scope = "fleet",
        FleetFailMode = failMode,
        LocalShare = localShare,
        Key = "route",
    };

    private static Request Req() => new() { Method = "GET", RequestUriString8 = (ByteString)"/x", Host = "app" };

    private static bool Denied(ReverseProxyOptions options)
    {
        var denied = RoutePolicies.TryHandleLite(
            options, EndPoint, Req(), new IPEndPoint(IPAddress.Loopback, 1000),
            out var status, out _, out _);
        if (denied)
        {
            Assert.AreEqual(429, status);
        }

        return denied;
    }

    [TestMethod]
    public void ScopeNode_IsUnchanged_ByteIdenticalPath()
    {
        var policy = new RoutePolicyConfig
        {
            Name = "rate",
            Kind = "rateLimit",
            Limit = 2,
            WindowSeconds = 10,
            Key = "route",
        };
        var options = Options(policy);
        Assert.IsFalse(Denied(options));
        Assert.IsFalse(Denied(options));
        Assert.IsTrue(Denied(options));
        Assert.IsFalse(RoutePolicies.RequiresSessionPath(options));
    }

    [TestMethod]
    public void Fleet_WithNullStore_LocalFailMode_UsesLocalShare()
    {
        // limit 8, localShare 0.25 → local limit 2
        var options = Options(Fleet(limit: 8, failMode: "local", localShare: 0.25));
        Assert.IsFalse(Denied(options));
        Assert.IsFalse(Denied(options));
        Assert.IsTrue(Denied(options));
        Assert.IsFalse(RoutePolicies.RequiresSessionPath(options));
    }

    [TestMethod]
    public void Fleet_OpenFailMode_AllowsWhenStoreMissing()
    {
        var options = Options(Fleet(limit: 1, failMode: "open"));
        for (var i = 0; i < 20; i++)
        {
            Assert.IsFalse(Denied(options));
        }
    }

    [TestMethod]
    public void Fleet_ClosedFailMode_DeniesWhenStoreMissing()
    {
        var options = Options(Fleet(limit: 100, failMode: "closed"));
        Assert.IsTrue(Denied(options));
    }

    [TestMethod]
    public async Task Fleet_WithFakeStore_SharesBudgetAcrossReserves()
    {
        var store = new FakeFleetRateStore();
        var options = Options(Fleet(limit: 5, burst: 5, failMode: "closed"), store);

        // Kick the background refill and wait for tokens.
        _ = Denied(options);
        for (var i = 0; i < 50 && store.LeaseCalls == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.IsTrue(store.LeaseCalls > 0, "background refill must call LeaseAsync");

        // Drain the leased tokens; closed fail-mode denies once the reserve and store are empty.
        var allowed = 0;
        for (var i = 0; i < 40; i++)
        {
            if (!Denied(options))
            {
                allowed++;
            }

            await Task.Delay(5);
        }

        Assert.IsTrue(allowed <= 5 + 5, "overshoot bound: limit + burst (single node, one lease)");
        Assert.IsTrue(allowed >= 1, "at least one request should be allowed after refill");
    }

    [TestMethod]
    public void Fleet_DoesNotRequireSessionPath()
    {
        var options = Options(Fleet(limit: 100), new FakeFleetRateStore());
        Assert.IsFalse(RoutePolicies.RequiresSessionPath(options));
        Assert.IsFalse(RoutePolicies.HoldsConnectionSlot(options));
    }

    [TestMethod]
    public async Task FakeStore_LeaseMath_GrantsUpToWant()
    {
        var store = new FakeFleetRateStore();
        var spec = new FleetRateSpec { Limit = 10, WindowSeconds = 60, Burst = 10, TokenBucket = true };
        var first = await store.LeaseAsync("k", 4, spec);
        Assert.IsTrue(first.StoreAvailable);
        Assert.AreEqual(4, first.Granted);
        var second = await store.LeaseAsync("k", 10, spec);
        Assert.AreEqual(6, second.Granted);
        var third = await store.LeaseAsync("k", 1, spec);
        Assert.AreEqual(0, third.Granted);
    }
}
