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
}
