using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Clusters;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

[TestClass]
[DoNotParallelize]
public class ResiliencePhaseProbeTests
{
    [TestMethod]
    public async Task ActiveProbe_EjectsAndRestoresOnlyDestinationsItMarked()
    {
        ActiveHealthProber.ProbeOverride = (_, _) => Task.FromResult(false);
        try
        {
            var manager = new ClusterManager();
            manager.ActiveHealthProbeProvider = new ActiveHealthProbeProvider();
            var cluster = Cluster();
            cluster = new ClusterConfig
            {
                Id = "c",
                Retry = cluster.Retry,
                HealthCheck = new HealthCheckConfig
                {
                    Active = new ActiveHealthConfig { Enabled = true, HealthyThreshold = 1, UnhealthyThreshold = 1 },
                },
                Destinations = cluster.Destinations,
            };
            await manager.ApplyAsync([cluster]);
            Assert.AreEqual(1, manager.ActiveBackgroundTaskCount);
            await manager.ProbeActiveOnceAsync();
            Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("a"));

            manager.SetDestinationState("b", DestinationState.Maintenance);
            ActiveHealthProber.ProbeOverride = (_, _) => Task.FromResult(true);
            await manager.ProbeActiveOnceAsync();
            Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("a"));
            Assert.AreEqual(DestinationState.Maintenance, manager.GetDestinationState("b"));
            await manager.DisposeAsync();
            Assert.AreEqual(0, manager.ActiveBackgroundTaskCount);
        }
        finally
        {
            ActiveHealthProber.ProbeOverride = null;
        }
    }

    private static ClusterConfig Cluster() => new()
    {
        Id = "c",
        Retry = new RetryConfig { Attempts = 1 },
        Destinations =
        [
            new DestinationConfig { Id = "a", Address = "10.0.0.1", Port = 80, MaxConcurrentRequests = 1, Priority = 0 },
            new DestinationConfig { Id = "b", Address = "10.0.0.2", Port = 80, Priority = 0 },
            new DestinationConfig { Id = "backup", Address = "10.0.0.3", Port = 80, Backup = true },
        ],
    };
}
