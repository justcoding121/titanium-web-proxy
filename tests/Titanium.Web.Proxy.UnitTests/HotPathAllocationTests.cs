using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
/// Allocation ceilings recorded for reverse-proxy dispatch. Later phases may only lower these numbers.
/// Baseline SHA 8a46052b, measured after Phase 0 routing fixes on 2026-10-24 (Release).
/// </summary>
[TestClass]
[DoNotParallelize]
public class HotPathAllocationTests
{
    [TestMethod]
    public async Task Resolve_OneRoute_StaysWithinBudget()
    {
#if DEBUG
        Assert.Inconclusive("Allocation ceilings are enforced in Release.");
#else
        var manager = new ClusterManager();
        var cluster = new ClusterConfig
        {
            Id = "c",
            Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 80 }],
        };
        await manager.ApplyAsync([cluster]);
        var options = new Abstractions.ReverseProxyOptions
        {
            Routes = [new RouteConfig { Id = "r", ClusterId = "c", Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix } }],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
        };
        var request = new Request { Method = "GET", RequestUriString8 = (ByteString)"/health", Host = "app" };
        const int n = 10_000;
        for (var i = 0; i < 5_000; i++)
        {
            DestinationResolver.Resolve(options, request, null, 80, out _, out _);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < n; i++)
        {
            DestinationResolver.Resolve(options, request, null, 80, out _, out _);
        }

        var per = (GC.GetAllocatedBytesForCurrentThread() - before) / n;
        // One request-target string per call (GetString). Ceiling is the measured Phase 0 cost.
        Assert.IsTrue(per <= 320, $"resolve allocated {per} bytes/call");
#endif
    }

    [TestMethod]
    public void Match_EightRoutes_DoesNotAllocate()
    {
#if DEBUG
        Assert.Inconclusive("Allocation ceilings are enforced in Release.");
#else
        var routes = new RouteConfig[8];
        for (var i = 0; i < routes.Length; i++)
        {
            routes[i] = new RouteConfig
            {
                Id = "r" + i,
                ClusterId = "c",
                Order = i,
                Match = new RouteMatch { Path = "/p" + i, PathKind = PathMatchKind.Prefix },
            };
        }

        var matcher = new RouteMatcher();
        var ctx = new RouteMatchContext("h", "/p3/x", "GET", null, null);
        for (var i = 0; i < 5_000; i++)
        {
            matcher.Match(ctx, routes);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            matcher.Match(ctx, routes);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // IReadOnlyList foreach boxes its enumerator (pre-existing). Ceiling is 32 bytes/call.
        Assert.IsTrue(allocated / 10_000 <= 32, $"match allocated {allocated / 10_000} bytes/call");
#endif
    }

    [TestMethod]
    public async Task WarmQuicLookup_WithAReadyKey_DoesNotAllocate()
    {
#if DEBUG
        await Task.CompletedTask;
        Assert.Inconclusive("Allocation ceilings are enforced in Release.");
#else
        using var proxy = new ProxyServer(false, false, false);
        await using var pool = new QuicConnectionPool(proxy);
        const string key = "h3:localhost:443:localhost:";
        for (var i = 0; i < 1_000; i++)
        {
            pool.HasLiveConnection(key);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            pool.HasLiveConnection(key);
        }

        var per = (GC.GetAllocatedBytesForCurrentThread() - before) / 10_000;
        Assert.IsTrue(per <= 0, $"warm QUIC lookup allocated {per} bytes/call");
#endif
    }
}
