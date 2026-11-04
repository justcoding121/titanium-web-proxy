using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class ActiveHealthProbeBoundaryTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task WithoutProvider_ActiveHealthStartsNoProbe_AndDnsRefreshStillRuns()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                HealthCheck = new HealthCheckConfig
                {
                    Active = new ActiveHealthConfig { Enabled = true, IntervalSeconds = 3600, JitterPercent = 0 },
                },
                Destinations =
                [
                    new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 9, DnsRefreshSeconds = 1 },
                ],
            },
        ]);

        Assert.IsNull(manager.ActiveHealthProbeProvider);
        Assert.AreEqual(1, manager.ActiveBackgroundTaskCount, "DNS refresh is the only background task");

        var until = DateTime.UtcNow.AddSeconds(8);
        while (!manager.TryGetResolved("d", out _) && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(manager.TryGetResolved("d", out var address));
        Assert.IsNotNull(address);
        await manager.DisposeAsync();
        Assert.AreEqual(0, manager.ActiveBackgroundTaskCount);
    }

    [TestMethod]
    public async Task SettingProviderAfterApply_StartsEachProbeOnce()
    {
        var manager = new ClusterManager();
        var provider = new CountingProvider();
        await manager.ApplyAsync([ActiveCluster("a"), ActiveCluster("b")]);
        Assert.AreEqual(0, manager.ActiveBackgroundTaskCount);
        Assert.AreEqual(0, provider.StartCount);

        manager.ActiveHealthProbeProvider = provider;

        Assert.AreEqual(2, provider.StartCount);
        Assert.AreEqual(2, provider.Live);
        Assert.AreEqual(2, manager.ActiveBackgroundTaskCount);
        await manager.DisposeAsync();
        Assert.AreEqual(0, provider.Live);
    }

    [TestMethod]
    public async Task Reload_StopsAndRestartsBackgroundWork_WithoutLeakingProbes()
    {
        var manager = new ClusterManager();
        var provider = new CountingProvider();
        manager.ActiveHealthProbeProvider = provider;
        var cluster = new ClusterConfig
        {
            Id = "c",
            HealthCheck = new HealthCheckConfig
            {
                Active = new ActiveHealthConfig { Enabled = true, IntervalSeconds = 3600, JitterPercent = 0 },
            },
            Destinations =
            [
                new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 9, DnsRefreshSeconds = 30 },
            ],
        };

        await manager.ApplyAsync([cluster]);
        Assert.AreEqual(1, provider.StartCount);
        Assert.AreEqual(1, provider.Live);
        Assert.AreEqual(2, manager.ActiveBackgroundTaskCount);

        await manager.ApplyCoreAsync([cluster], reconcileBackground: false);
        Assert.AreEqual(1, provider.StartCount, "the temporary reload apply leaves probes running");
        Assert.AreEqual(1, provider.Live);
        Assert.AreEqual(2, manager.ActiveBackgroundTaskCount);

        await manager.ApplyAsync([cluster]);
        Assert.AreEqual(2, provider.StartCount);
        Assert.AreEqual(1, provider.Live);
        Assert.AreEqual(2, manager.ActiveBackgroundTaskCount);

        await manager.DisposeAsync();
        Assert.AreEqual(0, provider.Live);
        Assert.AreEqual(0, manager.ActiveBackgroundTaskCount);
    }

    private static ClusterConfig ActiveCluster(string id) => new()
    {
        Id = id,
        HealthCheck = new HealthCheckConfig
        {
            Active = new ActiveHealthConfig { Enabled = true, IntervalSeconds = 3600, JitterPercent = 0 },
        },
        Destinations = [new DestinationConfig { Id = id, Address = "10.0.0.1", Port = 80 }],
    };

    private sealed class CountingProvider : IActiveHealthProbeProvider
    {
        public int StartCount;
        public int Live;

        public IDisposable Start(ClusterConfig cluster, IClusterManager clusterManager)
        {
            Interlocked.Increment(ref StartCount);
            Interlocked.Increment(ref Live);
            return new Handle(this);
        }

        private sealed class Handle : IDisposable
        {
            private CountingProvider? _owner;

            public Handle(CountingProvider owner) => _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is not null)
                {
                    Interlocked.Decrement(ref owner.Live);
                }
            }
        }
    }
}
