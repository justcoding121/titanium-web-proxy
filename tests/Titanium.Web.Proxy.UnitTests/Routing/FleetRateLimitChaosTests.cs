using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.TestKit;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary>
/// Chaos rows for fleet rate limit: slow / down store (fault-injecting fake) and closed-port probe.
/// Never stops a docker Redis instance.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FleetRateLimitChaosTests
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

    private static RoutePolicyConfig Fleet(string failMode, int limit = 10) => new()
    {
        Name = "rate",
        Kind = "rateLimit",
        Limit = limit,
        WindowSeconds = 60,
        Burst = limit,
        Algorithm = "tokenBucket",
        Scope = "fleet",
        FleetFailMode = failMode,
        LocalShare = 0.25,
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
    public async Task SlowStore_DoesNotHangHotPath_AndEventuallyRefills()
    {
        var store = new FakeFleetRateStore { Delay = TimeSpan.FromMilliseconds(400) };
        var options = Options(Fleet("closed", limit: 5), store);

        // Hot path must return immediately even while a background lease is in flight.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = Denied(options);
        sw.Stop();
        Assert.IsTrue(sw.ElapsedMilliseconds < 200, $"hot path blocked for {sw.ElapsedMilliseconds}ms");

        await Task.Delay(700);
        Assert.IsTrue(store.LeaseCalls >= 1, "slow store must still be called for refill");
    }

    [TestMethod]
    public async Task DownStore_ClosedFailMode_Denies()
    {
        var store = new FakeFleetRateStore { AlwaysUnavailable = true };
        var options = Options(Fleet("closed", limit: 100), store);
        await WaitStoreMarkedDownAsync(options, store);

        var denied = false;
        for (var i = 0; i < 20; i++)
        {
            if (Denied(options))
            {
                denied = true;
                break;
            }
        }

        Assert.IsTrue(denied, "closed fail-mode must deny once the store is marked down");
    }

    [TestMethod]
    public async Task DownStore_OpenFailMode_Allows()
    {
        var store = new FakeFleetRateStore { AlwaysUnavailable = true };
        var options = Options(Fleet("open", limit: 1), store);
        await WaitStoreMarkedDownAsync(options, store);
        for (var i = 0; i < 20; i++)
        {
            Assert.IsFalse(Denied(options));
        }
    }

    [TestMethod]
    public async Task DownStore_LocalFailMode_UsesLocalShare()
    {
        // limit 8, localShare 0.25 → local limit 2
        var store = new FakeFleetRateStore { AlwaysUnavailable = true };
        var options = Options(Fleet("local", limit: 8), store);
        await WaitStoreMarkedDownAsync(options, store);
        Assert.IsFalse(Denied(options));
        Assert.IsFalse(Denied(options));
        Assert.IsTrue(Denied(options));
    }

    /// <summary>
    /// Cold fleet reserves start with StoreAvailable=true and Tokens=0, so the first miss waits on refill
    /// rather than fail-mode. Kick a lease and wait until AlwaysUnavailable flips the flag.
    /// </summary>
    private static async Task WaitStoreMarkedDownAsync(ReverseProxyOptions options, FakeFleetRateStore store)
    {
        _ = Denied(options);
        for (var i = 0; i < 50 && store.LeaseCalls == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.IsTrue(store.LeaseCalls > 0, "background refill must call the down store");
        await Task.Delay(30);
    }

    [TestMethod]
    public void ClosedPort_ConnectFails_WithoutTouchingDockerRedis()
    {
        // Bind then close so the port is free and refused — never stop a running redis container.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var client = new TcpClient();
        try
        {
            client.Connect(IPAddress.Loopback, port);
            Assert.Fail("expected connection refused on closed port");
        }
        catch (SocketException)
        {
            // expected
        }
    }
}
