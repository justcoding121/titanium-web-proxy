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

    [TestMethod]
    public void EmptyName_NacksWithNameFieldPath()
    {
        var result = RouteTranslator.Translate(new RouteConfiguration());
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("name", result.Error!.FieldPath);
    }

    [TestMethod]
    public void MissingMatch_NacksWithMatchFieldPath()
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
                    Routes = { new XdsRoute { RouteAction = new RouteAction { Cluster = "c1" } } },
                },
            },
        };

        var result = RouteTranslator.Translate(configuration);
        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "match");
    }

    [TestMethod]
    public void ExactPath_AndDirectResponse_Map()
    {
        var configuration = new RouteConfiguration
        {
            Name = "routes",
            VirtualHosts =
            {
                new VirtualHost
                {
                    Name = "www",
                    Routes =
                    {
                        new XdsRoute
                        {
                            Match = new XdsRouteMatch { Path = "/healthz" },
                            DirectResponse = new DirectResponseAction { Status = 204 },
                        },
                    },
                },
            },
        };

        var result = RouteTranslator.Translate(configuration);
        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        var route = result.Value!.Single();
        Assert.AreEqual("/healthz", route.Match.Path);
        Assert.AreEqual(PathMatchKind.Exact, route.Match.PathKind);
        Assert.IsNull(route.Match.Host);
        Assert.AreEqual(string.Empty, route.ClusterId);
        Assert.AreEqual(204, route.DirectResponse!.Status);
    }

    [TestMethod]
    public void RedirectAction_NacksWithRedirectFieldPath()
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
                            Match = new XdsRouteMatch { Prefix = "/" },
                            Redirect = new RedirectAction { HostRedirect = "elsewhere.example.com" },
                        },
                    },
                },
            },
        };

        var result = RouteTranslator.Translate(configuration);
        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "redirect");
    }
}
