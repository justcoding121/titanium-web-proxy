using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    [TestMethod]
    public void Regex_AGroupedAlternation_MatchesOnlyTheWholeValue()
    {
        var routes = new[] { Route("h", new HeaderValueMatch { Name = "x", Value = "(a)|(b)", Kind = HeaderMatchKind.Regex }) };
        Assert.AreEqual("h", Winner(routes, Headers("x", "a")));
        Assert.AreEqual("h", Winner(routes, Headers("x", "b")));
        Assert.IsNull(Winner(routes, Headers("x", "aEXTRA")));
        Assert.IsNull(Winner(routes, Headers("x", "EXTRAb")));
        Assert.IsNull(Winner(routes, Headers("x", "ab")));
    }

    [DataTestMethod]
    [DataRow("a)|(b")]
    [DataRow("a)(")]
    [DataRow("(")]
    [DataRow("(?=x)")]
    public void Regex_APatternThatCannotBeWrapped_NeverMatches(string pattern)
    {
        var routes = new[] { Route("h", new HeaderValueMatch { Name = "x", Value = pattern, Kind = HeaderMatchKind.Regex }) };
        foreach (var value in new[] { "a", "b", "aEXTRA", "EXTRAb", pattern, string.Empty })
        {
            Assert.IsNull(Winner(routes, Headers("x", value)), value);
        }
    }

    [TestMethod]
    public void HeadersMap_IsCaseInsensitive_HeaderMatchesExactIsNot()
    {
        var viaMap = new RouteConfig
        {
            Id = "map",
            ClusterId = "c",
            Match = new RouteMatch { Path = "/a", Headers = new Dictionary<string, string> { ["X-Env"] = "lab" } },
        };
        var viaList = Route("list", new HeaderValueMatch { Name = "X-Env", Value = "lab" });
        Assert.AreEqual("map", Winner([viaMap], Headers("x-env", "Lab")));
        Assert.IsNull(Winner([viaList], Headers("x-env", "Lab")));
    }

    [TestMethod]
    [Timeout(10_000)]
    public void Regex_ACatastrophicPatternOnALongValue_FinishesAndDoesNotMatch()
    {
        var routes = new[] { Route("h", new HeaderValueMatch { Name = "x", Value = "(a+)+b", Kind = HeaderMatchKind.Regex }) };
        var clock = Stopwatch.StartNew();
        Assert.IsNull(Winner(routes, Headers("x", new string('a', 50_000))));
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [TestMethod]
    public void NullHeaderMatches_AddNoPerCallAllocationToAWarmMatch()
    {
        // One route takes the direct path (no index), so only the new null check is under test.
        var routes = new[] { Route("plain") };
        var matcher = new RouteMatcher();
        var context = new RouteMatchContext("app.test", "/a", "GET", null, null);
        for (var i = 0; i < 1_000; i++)
        {
            Assert.IsNotNull(matcher.Match(context, routes));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            matcher.Match(context, routes);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allocated < 16 * 1024, $"allocated {allocated:N0} bytes over 10,000 matches");
    }
}
