using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Configuration;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ProxyConfigHostTests
{
    [TestMethod]
    public async Task Apply_PublishesRoutesAfterTheClusterUnion()
    {
        var manager = new ClusterManager();
        var host = new ProxyConfigHost();
        ReverseProxyOptions? published = null;
        var cluster = new ClusterConfig
        {
            Id = "c",
            Destinations = [new DestinationConfig { Id = "d", Address = "10.0.0.1", Port = 80 }],
        };
        var snapshot = new ProxyConfigSnapshot
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
            Clusters = [cluster],
        };

        await host.ApplyAsync(snapshot, manager, options => published = options);
        Assert.AreEqual("r", published?.Routes?[0].Id);
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d"));

        var provider = new InMemoryProxyConfigProvider();
        var seen = 0;
        using var _ = provider.Subscribe(_ => seen++);
        provider.Update(snapshot.Routes, snapshot.Clusters);
        Assert.AreEqual(1, seen);
        Assert.AreEqual(1, provider.Current.Generation);
    }
}
