using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Routing;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

[TestClass]
public class GatewayTranslatorRouteTests
{
    private static readonly string[] ExpectedClusterAddresses = ["10.0.0.1", "10.0.0.2"];
    private static readonly string[] AppExampleHost = ["app.example.com"];
    private static readonly string[] OnlyExampleHost = ["only.example.com"];

    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == StatusKind.HttpRoute && p.Name == route).Parents!.Single();

    private static RenderResult RenderRoute(StateBuilder b, HttpRoute route)
    {
        b.Routes.Add(route);
        var result = b.Render();
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));
        return result;
    }

    private static string? Winner(RenderResult result, string path, string host = "app.example.com", string method = "GET",
        Dictionary<string, string>? headers = null, Dictionary<string, string>? query = null) =>
        new RouteMatcher().Match(new RouteMatchContext(host, path, method, headers, query), result.Config.UserRoutes().ToList())?.Id;

    [TestMethod]
    public void UnmatchedRequests_GetA404_NeverAForwardToTheHostTheClientNamed()
    {
        var b = Basic();
        var result = RenderRoute(b, Route("r", "default", ["app.example.com"], Rule("web", 8080, PathPrefix("/api"))));
        var routes = result.Config.Routes.ToList();

        var matcher = new RouteMatcher();
        Assert.AreEqual("default/r/r0m0", matcher.Match(new RouteMatchContext("app.example.com", "/api/x", "GET", null, null), routes)?.Id);
        foreach (var (host, path) in new[] { ("app.example.com", "/other"), ("other.example.com", "/api"), ("172.19.255.200", "/") })
        {
            var hit = matcher.Match(new RouteMatchContext(host, path, "GET", null, null), routes);
            Assert.AreEqual(GatewayTranslator.NoMatchRouteId, hit?.Id, $"{host}{path}");
            Assert.AreEqual(404, hit!.DirectResponse?.Status);
        }

        Assert.AreEqual(GatewayTranslator.NoMatchRouteId, routes[^1].Id);
        Assert.IsTrue(routes[^1].Order > routes.Take(routes.Count - 1).Max(r => r.Order));
    }

    [TestMethod]
    public void Route_AttachesAndReportsAcceptedAndResolvedRefs()
    {
        var b = Basic();
        var result = RenderRoute(b, Route("r", "default", null, Rule("web")));

        var parent = Parent(result);
        Assert.AreEqual("True", Cond(parent.Conditions, "Accepted").Status);
        Assert.AreEqual("True", Cond(parent.Conditions, "ResolvedRefs").Status);
        Assert.AreEqual(StateBuilder.Controller, parent.ControllerName);
        var gateway = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway);
        Assert.AreEqual(1, gateway.Listeners!.Single().AttachedRoutes);

        var route = result.Config.UserRoutes().Single();
        Assert.AreEqual("default/r/r0m0", route.Id);
        Assert.AreEqual("default/web:8080", route.ClusterId);
        Assert.IsNull(route.Match.Path);
        Assert.IsNull(route.Clusters);
        var cluster = result.Config.Clusters.Single();
        CollectionAssert.AreEqual(ExpectedClusterAddresses, cluster.Destinations.Select(d => d.Address).ToArray());
        Assert.IsTrue(cluster.Destinations.All(d => d.Port == 8080));
    }

    [TestMethod]
    public void Route_ForAnotherGatewayOrUnknownParent_GetsNoStatus()
    {
        var b = Basic();
        var route = Route("r", "default", null, Rule("web"));
        route.Spec.ParentRefs = [new ParentReference { Name = "nope" }, new ParentReference { Name = "gw", Kind = "Service" }];
        var result = RenderRoute(b, route);

        Assert.IsFalse(result.StatusPatches.Any(p => p.Kind == StatusKind.HttpRoute));
        Assert.AreEqual(0, result.Config.UserRoutes().Count);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ParentRefWithoutSectionName_IsAcceptedWhenAnyListenerIntersects_WhicheverComesLast(bool matchingFirst)
    {
        var b = Basic();
        var match = Http("match", 80, "a.example.com");
        var other = Http("other", 80, "b.example.com");
        b.Gateways[0].Spec.Listeners = matchingFirst ? [match, other] : [other, match];
        var result = RenderRoute(b, Route("r", "default", ["a.example.com"], Rule("web")));

        var parent = Parent(result);
        Assert.AreEqual("True", Cond(parent.Conditions, "Accepted").Status);
        Assert.AreEqual("Accepted", Cond(parent.Conditions, "Accepted").Reason);
        var gateway = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!;
        Assert.AreEqual(1, gateway.Single(l => l.Name == "match").AttachedRoutes);
        Assert.AreEqual(0, gateway.Single(l => l.Name == "other").AttachedRoutes);
    }

    [TestMethod]
    public void ListenerWithAnUnresolvedCertificate_StillCountsItsAttachedRoutes()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Https("https", 443, null, "missing-cert")];
        var result = RenderRoute(b, Route("r", "default", null, Rule("web")));

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "ResolvedRefs").Status);
        Assert.AreEqual(1, listener.AttachedRoutes);
        Assert.AreEqual("Accepted", Cond(Parent(result).Conditions, "Accepted").Reason);
        Assert.AreEqual(0, result.Config.Listeners.Count, "the listener serves nothing until its certificate resolves");
    }

    [TestMethod]
    public void SectionName_ThatMatchesNothing_IsNoMatchingParent()
    {
        var b = Basic();
        var route = Route("r", "default", null, Rule("web"));
        route.Spec.ParentRefs![0].SectionName = "missing";
        var result = RenderRoute(b, route);

        var parent = Parent(result);
        Assert.AreEqual("False", Cond(parent.Conditions, "Accepted").Status);
        Assert.AreEqual("NoMatchingParent", Cond(parent.Conditions, "Accepted").Reason);
        Assert.AreEqual(0, result.Config.UserRoutes().Count);
    }

    [TestMethod]
    public void AllowedRoutes_DefaultsToTheGatewayNamespace()
    {
        var b = Basic();
        b.Namespaces.Add(new NamespaceInfo("apps", Labels("apps")));
        b.AddService("apps", "web", 8080, "10.1.0.1");
        var result = RenderRoute(b, Route("r", "apps", null, Rule("web")));

        Assert.AreEqual("NotAllowedByListeners", Cond(Parent(result).Conditions, "Accepted").Reason);
        Assert.AreEqual(0, result.Config.UserRoutes().Count);
    }

    [TestMethod]
    public void AllowedRoutes_AllAndSelector_AdmitOtherNamespaces()
    {
        var all = Basic();
        all.Namespaces.Add(new NamespaceInfo("apps", Labels("apps")));
        all.AddService("apps", "web", 8080, "10.1.0.1");
        all.Gateways[0].Spec.Listeners[0].AllowedRoutes = new AllowedRoutes { Namespaces = new RouteNamespaces { From = "All" } };
        Assert.AreEqual(1, RenderRoute(all, Route("r", "apps", null, Rule("web"))).Config.UserRoutes().Count);

        var selected = Basic();
        selected.Namespaces.Add(new NamespaceInfo("apps", new Dictionary<string, string> { ["team"] = "blue" }));
        selected.AddService("apps", "web", 8080, "10.1.0.1");
        selected.Gateways[0].Spec.Listeners[0].AllowedRoutes = new AllowedRoutes
        {
            Namespaces = new RouteNamespaces
            {
                From = "Selector",
                Selector = new LabelSelector { MatchLabels = new Dictionary<string, string> { ["team"] = "blue" } },
            },
        };
        Assert.AreEqual(1, RenderRoute(selected, Route("r", "apps", null, Rule("web"))).Config.UserRoutes().Count);

        selected.Namespaces[1] = new NamespaceInfo("apps", new Dictionary<string, string> { ["team"] = "red" });
        selected.Routes.Clear();
        Assert.AreEqual(0, RenderRoute(selected, Route("r", "apps", null, Rule("web"))).Config.UserRoutes().Count);
    }

    [TestMethod]
    public void AllowedRoutes_KindsWithoutHttpRoute_InvalidatesTheListener()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners[0].AllowedRoutes = new AllowedRoutes { Kinds = [new RouteGroupKind { Kind = "TCPRoute" }] };
        var result = RenderRoute(b, Route("r", "default", null, Rule("web")));

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual(0, listener.SupportedKinds.Count);
        Assert.AreEqual("InvalidRouteKinds", Cond(listener.Conditions, "ResolvedRefs").Reason);
    }

    [TestMethod]
    public void Hostnames_IntersectWithTheListener()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners[0].Hostname = "*.example.com";
        var result = RenderRoute(b, Route("r", "default", ["app.example.com", "other.org"], Rule("web")));

        CollectionAssert.AreEqual(AppExampleHost, result.Config.UserRoutes().Single().Match.Hosts!.ToArray());
    }

    [TestMethod]
    public void Hostnames_WithNoIntersection_AreNoMatchingListenerHostname()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners[0].Hostname = "a.example.com";
        var result = RenderRoute(b, Route("r", "default", ["b.example.com"], Rule("web")));

        Assert.AreEqual("NoMatchingListenerHostname", Cond(Parent(result).Conditions, "Accepted").Reason);
    }

    [TestMethod]
    public void ListenerHostname_IsInheritedWhenTheRouteHasNone()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners[0].Hostname = "only.example.com";
        var result = RenderRoute(b, Route("r", "default", null, Rule("web")));

        CollectionAssert.AreEqual(OnlyExampleHost, result.Config.UserRoutes().Single().Match.Hosts!.ToArray());
    }

    [TestMethod]
    public void PathMatches_MapToTheDataPlaneKinds()
    {
        var b = Basic();
        var rule = Rule(
            "web",
            8080,
            PathExact("/exact"),
            PathPrefix("/api/"),
            PathPrefix("/"),
            new HttpRouteMatch { Path = new HttpPathMatch { Type = "RegularExpression", Value = "/v[0-9]+/.*" } });
        var routes = RenderRoute(b, Route("r", "default", null, rule)).Config.UserRoutes().OrderBy(r => r.Id).ToList();

        Assert.AreEqual((PathMatchKind.Exact, "/exact"), (routes[0].Match.PathKind, routes[0].Match.Path));
        Assert.AreEqual((PathMatchKind.SegmentPrefix, "/api"), (routes[1].Match.PathKind, routes[1].Match.Path));
        Assert.IsNull(routes[2].Match.Path);
        Assert.AreEqual((PathMatchKind.Regex, "^(?:/v[0-9]+/.*)$"), (routes[3].Match.PathKind, routes[3].Match.Path));
    }

    [TestMethod]
    public void HeaderMethodAndQueryMatches_AreCarried()
    {
        var b = Basic();
        var match = new HttpRouteMatch
        {
            Method = "post",
            Headers = [new HttpHeaderMatch { Name = "X-Env", Value = "lab" }],
            QueryParams =
            [
                new HttpQueryParamMatch { Name = "a", Value = "1" },
                new HttpQueryParamMatch { Name = "v", Value = "[0-9]+", Type = "RegularExpression" },
            ],
        };
        var route = RenderRoute(b, Route("r", "default", null, Rule("web", 8080, match))).Config.UserRoutes().Single();

        Assert.AreEqual("POST", route.Match.Method);
        Assert.AreEqual("lab", route.Match.Headers!["x-env"]);
        Assert.AreEqual(QueryMatchKind.Exact, route.Match.QueryParameters![0].Kind);
        Assert.AreEqual(QueryMatchKind.Regex, route.Match.QueryParameters[1].Kind);
    }

    [TestMethod]
    public void HeaderRegex_IsUnsupportedAndAnswers500()
    {
        var b = Basic();
        var match = new HttpRouteMatch { Headers = [new HttpHeaderMatch { Name = "x", Value = ".*", Type = "RegularExpression" }] };
        var result = RenderRoute(b, Route("r", "default", null, Rule("web", 8080, match)));

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        Assert.AreEqual("True", Cond(Parent(result).Conditions, "PartiallyInvalid").Status);
        Assert.IsTrue(result.Notes.Any(n => n.Contains("header match", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UncompilableRegex_IsUnsupportedNotAConfigError()
    {
        var b = Basic();
        var match = new HttpRouteMatch { Path = new HttpPathMatch { Type = "RegularExpression", Value = "(?=x)" } };
        var result = RenderRoute(b, Route("r", "default", null, Rule("web", 8080, match)));

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void Precedence_FollowsTheSpecAndTheDataPlaneAgrees()
    {
        var b = Basic();
        var older = Route("older", "default", null, Rule("web", 8080, PathPrefix("/same")));
        older.Metadata.CreationTimestamp = T0.AddDays(-1);
        var newer = Route("newer", "default", null, Rule("web", 8080, PathPrefix("/same")));
        b.Routes.Add(newer);
        b.Routes.Add(older);
        b.Routes.Add(Route("exact", "default", null, Rule("web", 8080, PathExact("/same/a"))));
        b.Routes.Add(Route("long", "default", null, Rule("web", 8080, PathPrefix("/same/a/b"))));
        b.Routes.Add(Route("catchall", "default", null, Rule("web", 8080, PathPrefix("/"))));
        b.Routes.Add(Route(
            "method",
            "default",
            null,
            Rule("web", 8080, new HttpRouteMatch { Path = new HttpPathMatch { Type = "PathPrefix", Value = "/m" }, Method = "POST" }),
            Rule("web", 8080, PathPrefix("/m"))));
        b.Routes.Add(Route(
            "headers",
            "default",
            null,
            Rule("web", 8080, new HttpRouteMatch { Path = new HttpPathMatch { Value = "/h" }, Headers = [new HttpHeaderMatch { Name = "a", Value = "1" }] }),
            Rule("web", 8080, new HttpRouteMatch { Path = new HttpPathMatch { Value = "/h" }, Headers = [new HttpHeaderMatch { Name = "a", Value = "1" }, new HttpHeaderMatch { Name = "b", Value = "2" }] })));
        var result = b.Render();

        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count);
        Assert.AreEqual("default/exact/r0m0", Winner(result, "/same/a"));
        Assert.AreEqual("default/long/r0m0", Winner(result, "/same/a/b/c"));
        Assert.AreEqual("default/older/r0m0", Winner(result, "/same/zzz"), "oldest wins an exact tie");
        Assert.AreEqual("default/catchall/r0m0", Winner(result, "/other"));
        Assert.AreEqual("default/method/r0m0", Winner(result, "/m", method: "POST"));
        Assert.AreEqual("default/method/r1m0", Winner(result, "/m", method: "GET"));
        Assert.AreEqual("default/headers/r0m0", Winner(result, "/h", headers: new() { ["a"] = "1" }));
        Assert.AreEqual("default/headers/r1m0", Winner(result, "/h", headers: new() { ["a"] = "1", ["b"] = "2" }));
        var orders = result.Config.UserRoutes().Select(r => r.Order).ToList();
        CollectionAssert.AllItemsAreUnique(orders);
    }

    [TestMethod]
    public void Precedence_ExactHostnameBeatsWildcardAndNoHostname()
    {
        var b = Basic();
        b.Routes.Add(Route("any", "default", null, Rule("web", 8080, PathPrefix("/"))));
        b.Routes.Add(Route("wild", "default", ["*.example.com"], Rule("web", 8080, PathPrefix("/"))));
        b.Routes.Add(Route("exact", "default", ["app.example.com"], Rule("web", 8080, PathPrefix("/"))));
        var result = b.Render();

        Assert.AreEqual("default/exact/r0m0", Winner(result, "/", "app.example.com"));
        Assert.AreEqual("default/wild/r0m0", Winner(result, "/", "x.example.com"));
        Assert.AreEqual("default/any/r0m0", Winner(result, "/", "elsewhere.org"));
    }

    [TestMethod]
    public void QueryParameters_ReachTheMatcher()
    {
        var b = Basic();
        var match = new HttpRouteMatch
        {
            Path = new HttpPathMatch { Value = "/q" },
            QueryParams = [new HttpQueryParamMatch { Name = "env", Value = "lab" }],
        };
        b.Routes.Add(Route("withquery", "default", null, Rule("web", 8080, match)));
        b.Routes.Add(Route("plain", "default", null, Rule("web", 8080, PathPrefix("/q"))));
        var result = b.Render();

        Assert.AreEqual("default/withquery/r0m0", Winner(result, "/q", query: new() { ["env"] = "lab" }));
        Assert.AreEqual("default/plain/r0m0", Winner(result, "/q", query: new() { ["env"] = "prod" }));
    }
}
