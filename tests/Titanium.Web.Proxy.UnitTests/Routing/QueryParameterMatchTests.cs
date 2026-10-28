using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class QueryParameterMatchTests
{
    private static RouteConfig Route(string id, params QueryParameterMatch[]? entries) => new()
    {
        Id = id,
        ClusterId = "c",
        Match = new RouteMatch { Path = "/a", QueryParameters = entries },
    };

    private static string? Winner(IReadOnlyList<RouteConfig> routes, IReadOnlyDictionary<string, string>? query) =>
        new RouteMatcher().Match(new RouteMatchContext("app.test", "/a", "GET", null, query), routes)?.Id;

    [TestMethod]
    public void Default_IsNullAndAddsNoConstraint()
    {
        Assert.IsNull(new RouteMatch().QueryParameters);
        Assert.AreEqual("plain", Winner([Route("plain")], null));
    }

    [TestMethod]
    public void Exact_RequiresTheNameAndTheCaseSensitiveValue()
    {
        var routes = new[] { Route("q", new QueryParameterMatch { Name = "env", Value = "lab" }) };
        Assert.AreEqual("q", Winner(routes, new Dictionary<string, string> { ["env"] = "lab" }));
        Assert.IsNull(Winner(routes, new Dictionary<string, string> { ["env"] = "Lab" }));
        Assert.IsNull(Winner(routes, new Dictionary<string, string> { ["other"] = "lab" }));
        Assert.IsNull(Winner(routes, null));
    }

    [TestMethod]
    public void Regex_MatchesTheWholeValueOnly()
    {
        var routes = new[]
        {
            Route("q", new QueryParameterMatch { Name = "v", Value = "[0-9]+", Kind = QueryMatchKind.Regex }),
        };
        Assert.AreEqual("q", Winner(routes, new Dictionary<string, string> { ["v"] = "123" }));
        Assert.IsNull(Winner(routes, new Dictionary<string, string> { ["v"] = "123x" }));
        Assert.IsNull(Winner(routes, new Dictionary<string, string> { ["v"] = "x123" }));
    }

    [TestMethod]
    public void EveryEntryMustMatch_AndMoreConstraintsWinATie()
    {
        var routes = new[]
        {
            Route("one", new QueryParameterMatch { Name = "a", Value = "1" }),
            Route(
                "two",
                new QueryParameterMatch { Name = "a", Value = "1" },
                new QueryParameterMatch { Name = "b", Value = "2" }),
        };
        Assert.AreEqual("two", Winner(routes, new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }));
        Assert.AreEqual("one", Winner(routes, new Dictionary<string, string> { ["a"] = "1", ["b"] = "3" }));
    }

    [TestMethod]
    public void BrokenRegex_NeverMatches()
    {
        var routes = new[]
        {
            Route("q", new QueryParameterMatch { Name = "v", Value = "(", Kind = QueryMatchKind.Regex }),
        };
        Assert.IsNull(Winner(routes, new Dictionary<string, string> { ["v"] = "(" }));
    }
}
