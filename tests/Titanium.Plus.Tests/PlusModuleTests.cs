using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

[TestClass]
public class PlusModuleTests
{
    [TestMethod]
    public void RequiredAbstractionsVersion_Is70()
    {
        ITitaniumPlusModule module = new TitaniumPlusModule();
        Assert.AreEqual(new Version(7, 0, 0), module.RequiredAbstractionsVersion);
    }

    [TestMethod]
    public async Task DrainOperations_SetsDestinationState()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 },
                ],
            },
        ]);

        var ops = new DrainOperations(manager);
        ops.Drain("d1");
        Assert.AreEqual(DestinationState.Draining, manager.GetDestinationState("d1"));
    }

    [TestMethod]
    public async Task PrometheusExporter_RendersDestinationGauge()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 },
                ],
            },
        ]);

        var text = new PrometheusMetricsExporter(manager, null).Render();
        StringAssert.Contains(text, "titanium_destination_state");
        StringAssert.Contains(text, "d1");
    }
}
