using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
[DoNotParallelize]
public class ClusterHealthProberTests
{
    [TestCleanup]
    public void Restore() => ClusterHealthProber.ProbeOverride = null;

    private static ClusterConfig Cluster(ActiveHealthConfig active, params DestinationConfig[] destinations) => new()
    {
        Id = "c",
        Destinations = destinations,
        HealthCheck = new HealthCheckConfig { Active = active },
    };

    private static DestinationConfig Dest(string id, int port = 80) => new() { Id = id, Address = "127.0.0.1", Port = port };

    /// <summary>The manager gets a plain copy so it does not start its own probe loop.</summary>
    private static async Task<ClusterManager> ManagerAsync(ClusterConfig probed)
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync([new ClusterConfig { Id = probed.Id, Destinations = probed.Destinations }]);
        return manager;
    }

    private static async Task<(TcpListener Listener, Task Serve)> HttpOriginAsync(string status, int expectedRequests)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            for (var i = 0; i < expectedRequests; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[2048];
                _ = await stream.ReadAsync(buffer);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            }
        });
        await Task.Yield();
        return (listener, serve);
    }

    [TestMethod]
    public async Task ThresholdsDecideWhenADestinationLeavesAndRejoins()
    {
        var healthy = true;
        ClusterHealthProber.ProbeOverride = (dest, _) => Task.FromResult(dest.Id == "a" || healthy);
        var cluster = Cluster(new ActiveHealthConfig { Enabled = true, IntervalSeconds = 3600, UnhealthyThreshold = 2, HealthyThreshold = 2 }, Dest("a"), Dest("b"));
        var manager = await ManagerAsync(cluster);
        using var prober = new ClusterHealthProber(manager, cluster, TimeProvider.System);

        healthy = false;
        await prober.ProbeOnceAsync();
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("b"), "one failure is below the threshold");
        await prober.ProbeOnceAsync();
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("b"));
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("a"));

        healthy = true;
        await prober.ProbeOnceAsync();
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("b"), "one success is below the threshold");
        await prober.ProbeOnceAsync();
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("b"));
    }

    [TestMethod]
    public async Task ADestinationEjectedByAnotherSourceIsNotRestoredByTheProbe()
    {
        ClusterHealthProber.ProbeOverride = (_, _) => Task.FromResult(true);
        var cluster = Cluster(new ActiveHealthConfig { Enabled = true, IntervalSeconds = 3600, HealthyThreshold = 1 }, Dest("a"));
        var manager = await ManagerAsync(cluster);
        manager.SetDestinationState("a", DestinationState.Maintenance);
        using var prober = new ClusterHealthProber(manager, cluster, TimeProvider.System);
        await prober.ProbeOnceAsync();
        Assert.AreEqual(DestinationState.Maintenance, manager.GetDestinationState("a"));
    }

    [TestMethod]
    public async Task DisabledProbe_DoesNothing()
    {
        var calls = 0;
        ClusterHealthProber.ProbeOverride = (_, _) =>
        {
            calls++;
            return Task.FromResult(false);
        };
        var cluster = Cluster(new ActiveHealthConfig { Enabled = false }, Dest("a"));
        var manager = await ManagerAsync(cluster);
        using var prober = new ClusterHealthProber(manager, cluster, TimeProvider.System);
        await prober.ProbeOnceAsync();
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task TcpProbe_SucceedsAgainstAListenerAndFailsWhenClosed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var cluster = Cluster(
            new ActiveHealthConfig { Enabled = true, IntervalSeconds = 3600, Kind = ActiveHealthKind.Tcp, TimeoutSeconds = 2, UnhealthyThreshold = 1, HealthyThreshold = 1 },
            Dest("up", port));
        var manager = await ManagerAsync(cluster);
        using var prober = new ClusterHealthProber(manager, cluster, TimeProvider.System);
        await prober.ProbeOnceAsync();
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("up"));

        listener.Stop();
        await prober.ProbeOnceAsync();
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("up"));
    }

    [TestMethod]
    public async Task HttpProbe_AcceptsTheDefaultSuccessRange()
    {
        var (listener, serve) = await HttpOriginAsync("204 No Content", 1);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var cluster = Cluster(
                new ActiveHealthConfig { Enabled = true, IntervalSeconds = 3600, TimeoutSeconds = 3, Path = "/health", Query = "deep=1", UnhealthyThreshold = 1, HealthyThreshold = 1 },
                Dest("web", port));
            var manager = await ManagerAsync(cluster);
            manager.SetDestinationState("web", DestinationState.Unhealthy);
            using var prober = new ClusterHealthProber(manager, cluster, TimeProvider.System);
            await prober.ProbeOnceAsync();
            await serve;
            Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("web"), "only probe-ejected destinations are restored");
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task HttpProbe_ExpectedStatusListIsExact()
    {
        var (listener, serve) = await HttpOriginAsync("200 OK", 2);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var strict = Cluster(
                new ActiveHealthConfig { Enabled = true, IntervalSeconds = 3600, TimeoutSeconds = 3, ExpectedStatus = [204], Query = "?x=1", UnhealthyThreshold = 1 },
                Dest("web", port));
            var manager = await ManagerAsync(strict);
            using var prober = new ClusterHealthProber(manager, strict, TimeProvider.System);
            await prober.ProbeOnceAsync();
            Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("web"), "200 is not in the expected list");

            var lenient = Cluster(
                new ActiveHealthConfig { Enabled = true, IntervalSeconds = 3600, TimeoutSeconds = 3, ExpectedStatus = [200, 204], UnhealthyThreshold = 1, HealthyThreshold = 1 },
                Dest("web", port));
            using var second = new ClusterHealthProber(manager, lenient, TimeProvider.System);
            await second.ProbeOnceAsync();
            await serve;
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task Dispose_StopsTheLoopAndCanRunTwice()
    {
        var cluster = Cluster(new ActiveHealthConfig { Enabled = true, IntervalSeconds = 3600, JitterPercent = 50 }, Dest("a"));
        var manager = await ManagerAsync(cluster);
        var prober = new ClusterHealthProber(manager, cluster, TimeProvider.System);
        await Task.Delay(30);
        prober.Dispose();
        prober.Dispose();
    }
}
