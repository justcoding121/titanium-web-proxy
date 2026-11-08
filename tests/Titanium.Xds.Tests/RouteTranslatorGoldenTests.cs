using Envoy.Config.Route.V3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Xds.Translation;
using XdsRoute = Envoy.Config.Route.V3.Route;
using XdsRouteMatch = Envoy.Config.Route.V3.RouteMatch;

namespace Titanium.Xds.Tests;

[TestClass]
public class RouteTranslatorGoldenTests
{
    [TestMethod]
    public void RouteAction_MapsClusterAndPrefixMatch()
    {
        var configuration = new RouteConfiguration
        {
            Name = "routes",
            VirtualHosts =
            {
                new VirtualHost
                {
                    Name = "www",
                    Domains = { "app.example.com" },
                    Routes =
                    {
                        new XdsRoute
                        {
                            Match = new XdsRouteMatch { Prefix = "/api" },
                            RouteAction = new RouteAction { Cluster = "c1" },
                        },
                    },
                },
            },
        };

        var result = RouteTranslator.Translate(configuration);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        var route = result.Value!.Single();
        Assert.AreEqual("c1", route.ClusterId);
        Assert.AreEqual("/api", route.Match.Path);
        Assert.AreEqual(PathMatchKind.Prefix, route.Match.PathKind);
        Assert.AreEqual("app.example.com", route.Match.Host);
    }

    [TestMethod]
    public void UnsupportedMatch_NacksWithFieldPath()
    {
        var configuration = new RouteConfiguration
        {
            Name = "routes",
            VirtualHosts =
            {
                new VirtualHost
                {
                    Name = "www",
                    Domains = { "*" },
                    Routes =
                    {
                        new XdsRoute
                        {
                            Match = new XdsRouteMatch(),
                            RouteAction = new RouteAction { Cluster = "c1" },
                        },
                    },
                },
            },
        };

        var result = RouteTranslator.Translate(configuration);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "match");
    }
}
