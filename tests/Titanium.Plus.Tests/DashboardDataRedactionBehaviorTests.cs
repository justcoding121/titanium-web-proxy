using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Dashboard;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Tests;

/// <summary>
///     Dashboard view builders must redact credential-bearing match and transform values.
///     Bug classes: query/header values or secret transform params appear in HTML/API.
/// </summary>
[TestClass]
public class DashboardDataRedactionBehaviorTests
{
    [TestMethod]
    public void Routes_DescribeMatch_ShowsNamesNotValues()
    {
        var page = DashboardData.Routes(
            clusters: null,
            routes:
            [
                new RouteConfig
                {
                    Id = "r1",
                    ClusterId = "c1",
                    Order = 1,
                    Match = new RouteMatch
                    {
                        Hosts = ["app.test", "api.test"],
                        Path = "/v1",
                        PathKind = PathMatchKind.Prefix,
                        Methods = ["GET", "POST"],
                        Headers = new Dictionary<string, string>
                        {
                            ["Authorization"] = "Bearer secret-token",
                            ["X-Trace"] = "1",
                        },
                        Query = new Dictionary<string, string> { ["token"] = "sekrit", ["q"] = "1" },
                        QueryParameters = [new QueryParameterMatch { Name = "sig", Value = ".*" }],
                    },
                },
            ],
            metrics: null);

        Assert.AreEqual(1, page.Routes.Count);
        var match = page.Routes[0].Match;
        StringAssert.Contains(match, "host app.test|api.test");
        StringAssert.Contains(match, "path prefix /v1");
        StringAssert.Contains(match, "method GET|POST");
        StringAssert.Contains(match, "headers Authorization,X-Trace");
        StringAssert.Contains(match, "query q,sig,token");
        Assert.IsFalse(match.Contains("Bearer", StringComparison.Ordinal));
        Assert.IsFalse(match.Contains("sekrit", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Routes_DescribeAction_CoversRedirectDirectSplitAndCluster()
    {
        var page = DashboardData.Routes(
            null,
            [
                new RouteConfig
                {
                    Id = "redirect",
                    ClusterId = "",
                    Order = 1,
                    Match = new RouteMatch(),
                    Redirect = new RedirectConfig { Status = 301 },
                },
                new RouteConfig
                {
                    Id = "direct",
                    ClusterId = "",
                    Order = 2,
                    Match = new RouteMatch { Path = "/" },
                    DirectResponse = new DirectResponseConfig { Status = 204 },
                },
                new RouteConfig
                {
                    Id = "split",
                    ClusterId = "c1",
                    Order = 3,
                    Match = new RouteMatch(),
                    Clusters =
                    [
                        new WeightedCluster { ClusterId = "a", Weight = 80 },
                        new WeightedCluster { ClusterId = "b", Weight = 20 },
                    ],
                },
                new RouteConfig
                {
                    Id = "cluster",
                    ClusterId = "c1",
                    Order = 4,
                    Match = new RouteMatch(),
                },
            ],
            null);

        Assert.AreEqual("redirect 301", page.Routes[0].Action);
        Assert.AreEqual("direct 204", page.Routes[1].Action);
        Assert.AreEqual("split a:80, b:20", page.Routes[2].Action);
        Assert.AreEqual("cluster", page.Routes[3].Action);
        Assert.AreEqual("any", page.Routes[0].Match);
    }

    [TestMethod]
    public void Routes_DescribeTransform_MasksSecretParameters()
    {
        var page = DashboardData.Routes(
            null,
            [
                new RouteConfig
                {
                    Id = "t",
                    ClusterId = "c1",
                    Order = 1,
                    Match = new RouteMatch(),
                    Transforms =
                    [
                        new TransformConfig { Kind = "RequestHeader" },
                        new TransformConfig
                        {
                            Kind = "RequestHeader",
                            Parameters = new Dictionary<string, string>
                            {
                                ["name"] = "X-Auth",
                                ["value"] = "super-secret",
                                ["path"] = "/keep",
                            },
                        },
                    ],
                },
            ],
            null);

        CollectionAssert.AreEqual(
            new[] { "RequestHeader", "RequestHeader(name=X-Auth, path=/keep, value=***)" },
            page.Routes[0].Transforms.ToArray());
    }
}
