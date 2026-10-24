using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class ResiliencePhaseTests
{
    [TestMethod]
    public async Task Runtime_IsReusedForStateChanges_AndRebuiltForANewCluster()
    {
        var manager = new ClusterManager();
        var cluster = Cluster();
        await manager.ApplyAsync([cluster]);
        var first = ClusterRuntime.Find(manager.Snapshot, "c");
        Assert.IsNotNull(first);
        Assert.IsTrue(first.HasLimits);
        Assert.IsTrue(first.HasPriorities);
        Assert.IsTrue(first.RetryEnabled);

        manager.SetDestinationState("a", DestinationState.Unhealthy);
        Assert.AreSame(first, ClusterRuntime.Find(manager.Snapshot, "c"));

        await manager.ApplyAsync([Cluster()]);
        Assert.AreNotSame(first, ClusterRuntime.Find(manager.Snapshot, "c"));
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task ActiveProbe_EjectsAndRestoresOnlyDestinationsItMarked()
    {
        ClusterHealthProber.ProbeOverride = (_, _) => Task.FromResult(false);
        try
        {
            var manager = new ClusterManager();
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
            ClusterHealthProber.ProbeOverride = (_, _) => Task.FromResult(true);
            await manager.ProbeActiveOnceAsync();
            Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("a"));
            Assert.AreEqual(DestinationState.Maintenance, manager.GetDestinationState("b"));
            await manager.DisposeAsync();
            Assert.AreEqual(0, manager.ActiveBackgroundTaskCount);
        }
        finally
        {
            ClusterHealthProber.ProbeOverride = null;
        }
    }

    [TestMethod]
    public async Task MaxConcurrent_AndPriority_AndSlowStart()
    {
        var time = new Support.ManualTimeProvider();
        var tracker = new DestinationHealthTracker(time);
        var balancer = new LoadBalancer(tracker);
        var manager = new ClusterManager();
        await manager.ApplyAsync([Cluster()]);
        using (tracker.TrackRequest("a"))
        {
            Assert.AreEqual("b", balancer.Select(Cluster(), manager.Snapshot)?.Id);
        }

        manager.SetDestinationState("a", DestinationState.Unhealthy);
        manager.SetDestinationState("b", DestinationState.Unhealthy);
        Assert.AreEqual("backup", balancer.Select(Cluster(), manager.Snapshot)?.Id);

        var heavy = new DestinationConfig
        {
            Id = "heavy",
            Address = "10.0.0.9",
            Port = 80,
            Weight = 10,
            SlowStartSeconds = 10,
        };
        Assert.AreEqual(1, tracker.EffectiveWeight(heavy, 0));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual(10, tracker.EffectiveWeight(heavy, 0));
    }

    [TestMethod]
    public async Task TrySelectAlternate_MovesToTheNextDestination_AndSkipsPost()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var manager = new ClusterManager();
        await manager.ApplyAsync([Cluster()]);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            ClusterManager = manager,
            LoadBalancer = new LoadBalancer(),
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Retry = new RetryConfig { Attempts = 1 },
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };

        var session = MakeSession(proxy, "GET");
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Routed, ReverseProxySessionDispatch.Apply(proxy, session));
        var first = session.UpstreamDestinationId;
        Assert.IsTrue(ReverseProxySessionDispatch.TrySelectAlternate(proxy, session));
        Assert.AreNotEqual(first, session.UpstreamDestinationId);

        var post = MakeSession(proxy, "POST");
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Routed, ReverseProxySessionDispatch.Apply(proxy, post));
        Assert.IsFalse(ReverseProxySessionDispatch.TrySelectAlternate(proxy, post));
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

    private static SessionEventArgs MakeSession(ProxyServer proxy, string method)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.Method = method;
        session.HttpClient.Request.RequestUriString = "http://example.test/";
        return session;
    }
}
