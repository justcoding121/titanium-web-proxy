using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class ConfigOverlayTests
{
    [TestMethod]
    public void Merge_UpsertAndRemove_AppliesInOrder()
    {
        var baseline = new TwpConfig
        {
            Routes =
            [
                new RouteConfig { Id = "r1", ClusterId = "c1", Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix } },
            ],
            Clusters =
            [
                new ClusterConfig
                {
                    Id = "c1",
                    Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 8080 }],
                },
            ],
        };
        var overlay = new ConfigOverlayDocument
        {
            Routes =
            {
                Upsert = [new RouteConfig { Id = "r2", ClusterId = "c1", Match = new RouteMatch { Path = "/x", PathKind = PathMatchKind.Prefix } }],
                Remove = ["ghost-route"],
            },
            Clusters = { Remove = ["c1"] },
        };
        var merged = ConfigOverlay.Merge(baseline, overlay);
        Assert.AreEqual(2, merged.Config.Routes.Count);
        Assert.AreEqual(0, merged.Config.Clusters.Count);
        Assert.IsTrue(merged.Orphans.Any(o => o.Contains("ghost-route", StringComparison.Ordinal)));
    }
}
