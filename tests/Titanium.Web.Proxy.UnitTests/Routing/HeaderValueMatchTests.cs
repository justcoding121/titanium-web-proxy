using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class HeaderValueMatchTests
{
    private static RouteConfig Route(string id, params HeaderValueMatch[]? entries) => new()
    {
        Id = id,
        ClusterId = "c",
        Match = new RouteMatch { Path = "/a", HeaderMatches = entries },
    };

    private static string? Winner(IReadOnlyList<RouteConfig> routes, IReadOnlyDictionary<string, string>? headers) =>
        new RouteMatcher().Match(new RouteMatchContext("app.test", "/a", "GET", headers, null), routes)?.Id;

    private static Dictionary<string, string> Headers(string name, string value) =>
        new(System.StringComparer.OrdinalIgnoreCase) { [name] = value };

    [TestMethod]
    public void Default_IsNullAndAddsNoConstraint()
    {
        Assert.IsNull(new RouteMatch().HeaderMatches);
        Assert.AreEqual("plain", Winner([Route("plain")], null));
    }

    [TestMethod]
    public void Exact_RequiresTheNameAndTheCaseSensitiveValue()
    {
        var routes = new[] { Route("h", new HeaderValueMatch { Name = "X-Env", Value = "lab" }) };
        Assert.AreEqual("h", Winner(routes, Headers("x-env", "lab")));
        Assert.IsNull(Winner(routes, Headers("x-env", "Lab")));
        Assert.IsNull(Winner(routes, Headers("other", "lab")));
        Assert.IsNull(Winner(routes, null));
    }

    [TestMethod]
    public void Regex_MatchesTheWholeValueOnly()
    {
        var routes = new[]
        {
            Route("h", new HeaderValueMatch { Name = "X-Build", Value = "v[0-9]+", Kind = HeaderMatchKind.Regex }),
        };
        Assert.AreEqual("h", Winner(routes, Headers("x-build", "v123")));
        Assert.IsNull(Winner(routes, Headers("x-build", "v123x")));
        Assert.IsNull(Winner(routes, Headers("x-build", "xv123")));
        Assert.IsNull(Winner(routes, Headers("x-build", "V123")));
    }

    [TestMethod]
    public void Alternation_IsAnchoredAsAGroup()
    {
        var routes = new[]
        {
            Route("h", new HeaderValueMatch { Name = "x", Value = "a|b", Kind = HeaderMatchKind.Regex }),
        };
        Assert.AreEqual("h", Winner(routes, Headers("x", "a")));
        Assert.AreEqual("h", Winner(routes, Headers("x", "b")));
        Assert.IsNull(Winner(routes, Headers("x", "ab")));
    }

    [TestMethod]
    public void EveryEntryMustMatch_AndMoreConstraintsWinATie()
    {
        var routes = new[]
        {
            Route("one", new HeaderValueMatch { Name = "a", Value = "1" }),
            Route(
                "two",
                new HeaderValueMatch { Name = "a", Value = "1" },
                new HeaderValueMatch { Name = "b", Value = "[0-9]", Kind = HeaderMatchKind.Regex }),
        };
        var both = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase) { ["a"] = "1", ["b"] = "2" };
        var onlyA = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase) { ["a"] = "1", ["b"] = "x" };
        Assert.AreEqual("two", Winner(routes, both));
        Assert.AreEqual("one", Winner(routes, onlyA));
    }

    [TestMethod]
    public void BrokenRegex_NeverMatches()
    {
        var routes = new[]
        {
            Route("h", new HeaderValueMatch { Name = "x", Value = "(", Kind = HeaderMatchKind.Regex }),
        };
        Assert.IsNull(Winner(routes, Headers("x", "(")));
    }
}
