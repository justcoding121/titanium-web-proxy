using Envoy.Config.Cluster.V3;
using Envoy.Service.Discovery.V3;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Xds.Ads;

namespace Titanium.Xds.Tests;

[TestClass]
public class AdsClientIntegrationTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task FakeAdsServer_AcksValidCluster()
    {
        await using var server = new FakeAdsServer();
        await server.StartAsync();
        var acked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = new AdsClient(new AdsClientOptions
        {
            ServerAddress = server.Address,
            Node = new Node { Id = "test", Cluster = "test" },
            ResourceTypeOrder = [Titanium.Xds.XdsTypeUrls.Cluster],
            SettleWindow = TimeSpan.FromMilliseconds(50),
        });

        client.ValidateSnapshot += _ =>
        {
            acked.TrySetResult();
            return Task.FromResult(new AdsAckDecision { Ack = true });
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        client.Start(cts.Token);
        await acked.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsTrue(client.Store.TryGetAll(Titanium.Xds.XdsTypeUrls.Cluster, out var clusters));
        Assert.IsTrue(clusters.Count > 0);

        var synced = SpinWait.SpinUntil(
            () => client.State == AdsConnectionState.Synced,
            TimeSpan.FromSeconds(5));
        Assert.IsTrue(synced, $"Expected Synced after ACK settle; was {client.State}");
    }

    [TestMethod]
    public void SnapshotBuilder_NacksLogicalDnsCluster()
    {
        var store = new AdsResourceStore();
        store.Replace(
            Titanium.Xds.XdsTypeUrls.Cluster,
            [Any.Pack(new Cluster { Name = "c1", Type = ClusterDiscoveryType.LogicalDns })]);

        var built = Titanium.Xds.XdsSnapshotBuilder.Build(store, Path.GetTempPath(), new Titanium.Web.Proxy.Configuration.Models.TwpConfig());
        Assert.IsFalse(built.IsSuccess);
        StringAssert.Contains(built.Error!.FieldPath, "type");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ValidateSnapshotReject_ClearsTypeAndDoesNotReachSynced()
    {
        // Bug class: a NACK still leaves the bad resources in the store / flips State to Synced.
        await using var server = new FakeAdsServer();
        await server.StartAsync();
        var nacked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = new AdsClient(new AdsClientOptions
        {
            ServerAddress = server.Address,
            Node = new Node { Id = "nack-test", Cluster = "test" },
            ResourceTypeOrder = [Titanium.Xds.XdsTypeUrls.Cluster],
            SettleWindow = TimeSpan.FromMilliseconds(50),
        });

        client.ValidateSnapshot += _ =>
        {
            nacked.TrySetResult();
            return Task.FromResult(new AdsAckDecision { Ack = false, ErrorDetail = "reject for coverage" });
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        client.Start(cts.Token);
        await nacked.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // After NACK the type is cleared from the store.
        await Task.Delay(200);
        Assert.IsFalse(
            client.Store.TryGetAll(Titanium.Xds.XdsTypeUrls.Cluster, out var clusters) && clusters.Count > 0,
            "NACK must ClearType so bad resources are not applied.");
        Assert.AreNotEqual(AdsConnectionState.Synced, client.State,
            "A rejected snapshot must not mark the client Synced.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task TooManyResources_NacksWithoutApplying()
    {
        // Bug class: MaxResourcesPerType is ignored and an oversized response is ACK'd.
        var state = new FakeAdsState();
        var resources = Enumerable.Range(0, 5)
            .Select(i => Any.Pack(new Cluster { Name = "c" + i, Type = ClusterDiscoveryType.Static }))
            .ToArray();
        state.Initial[Titanium.Xds.XdsTypeUrls.Cluster] = resources;

        await using var server = new FakeAdsServer(state);
        await server.StartAsync();
        var sawValidate = 0;

        await using var client = new AdsClient(new AdsClientOptions
        {
            ServerAddress = server.Address,
            Node = new Node { Id = "budget-test", Cluster = "test" },
            ResourceTypeOrder = [Titanium.Xds.XdsTypeUrls.Cluster],
            MaxResourcesPerType = 2,
            SettleWindow = TimeSpan.FromMilliseconds(50),
        });

        client.ValidateSnapshot += _ =>
        {
            Interlocked.Increment(ref sawValidate);
            return Task.FromResult(new AdsAckDecision { Ack = true });
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        client.Start(cts.Token);
        await Task.Delay(1500);

        Assert.AreEqual(0, Volatile.Read(ref sawValidate),
            "Oversized responses must NACK before ValidateSnapshot runs.");
        Assert.AreNotEqual(AdsConnectionState.Synced, client.State);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task UnreachableServer_MovesToDegradedThenRecoversOnCancel()
    {
        // Bug class: connection failures leave State stuck on Connecting forever.
        await using var client = new AdsClient(new AdsClientOptions
        {
            ServerAddress = "http://127.0.0.1:1",
            Node = new Node { Id = "down", Cluster = "test" },
            ResourceTypeOrder = [Titanium.Xds.XdsTypeUrls.Cluster],
            SettleWindow = TimeSpan.FromMilliseconds(10),
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        client.Start(cts.Token);
        var degraded = SpinWait.SpinUntil(
            () => client.State == AdsConnectionState.Degraded,
            TimeSpan.FromSeconds(5));
        Assert.IsTrue(degraded, $"Expected Degraded after unreachable ADS; was {client.State}");
        await client.StopAsync();
    }
}
