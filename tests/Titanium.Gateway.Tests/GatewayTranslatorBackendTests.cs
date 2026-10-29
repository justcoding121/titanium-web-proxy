using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

[TestClass]
public class GatewayTranslatorBackendTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RenderResult Render(StateBuilder b, HttpRoute route)
    {
        b.Routes.Add(route);
        var result = b.Render();
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));
        return result;
    }

    private static RouteParentStatus Parent(RenderResult result) =>
        result.StatusPatches.Single(p => p.Kind == StatusKind.HttpRoute).Parents!.Single();

    private static HttpRoute WithBackends(params HttpBackendRef[] refs) =>
        Route("r", "default", null, new HttpRouteRule { BackendRefs = refs.ToList() });

    [TestMethod]
    public void Weights_BecomeAWeightedClusterSplit()
    {
        var b = Basic();
        b.AddService("default", "canary", 8080, "10.0.1.1");
        var result = Render(b, WithBackends(
            new HttpBackendRef { Name = "web", Port = 8080, Weight = 90 },
            new HttpBackendRef { Name = "canary", Port = 8080, Weight = 10 }));

        var route = result.Config.UserRoutes().Single();
        Assert.AreEqual("default/web:8080", route.ClusterId);
        CollectionAssert.AreEqual(
            new[] { ("default/web:8080", 90), ("default/canary:8080", 10) },
            route.Clusters!.Select(c => (c.ClusterId, c.Weight)).ToArray());
        Assert.AreEqual(2, result.Config.Clusters.Count);
    }

    [TestMethod]
    public void ZeroWeight_ReceivesNoTraffic()
    {
        var b = Basic();
        b.AddService("default", "off", 8080, "10.0.2.1");
        var result = Render(b, WithBackends(
            new HttpBackendRef { Name = "web", Port = 8080 },
            new HttpBackendRef { Name = "off", Port = 8080, Weight = 0 }));

        Assert.IsNull(result.Config.UserRoutes().Single().Clusters);
        Assert.AreEqual(1, result.Config.Clusters.Count);
    }

    [TestMethod]
    public void OnlyReadyEndpoints_BecomeDestinations()
    {
        var b = Basic();
        b.Slices[0] = b.Slices[0] with
        {
            Endpoints =
            [
                new EndpointInfo(["10.0.0.1"], true),
                new EndpointInfo(["10.0.0.2"], false),
                new EndpointInfo(["10.0.0.3"], null),
            ],
        };
        var result = Render(b, WithBackends(new HttpBackendRef { Name = "web", Port = 8080 }));

        CollectionAssert.AreEqual(
            new[] { "10.0.0.1", "10.0.0.3" },
            result.Config.Clusters.Single().Destinations.Select(d => d.Address).ToArray());
    }

    [TestMethod]
    public void NamedServicePort_UsesTheSlicePortNumber()
    {
        var b = Basic();
        b.Services.RemoveAll(s => s.Name == "web");
        b.Services.Add(new ServiceInfo("default", "web", "ClusterIP", "10.96.1.1", [new ServicePortInfo("http", 80, null, "web")], []));
        b.Slices[0] = b.Slices[0] with
        {
            Ports = [new EndpointPortInfo("http", 9090, "TCP")],
            Endpoints = [new EndpointInfo(["10.0.0.1"], true)],
        };
        var result = Render(b, WithBackends(new HttpBackendRef { Name = "web", Port = 80 }));

        Assert.AreEqual(9090, result.Config.Clusters.Single().Destinations.Single().Port);
    }

    [TestMethod]
    public void NoReadyEndpoints_Answers500ForTheWholeRule()
    {
        var b = Basic();
        b.Slices[0] = b.Slices[0] with { Endpoints = [new EndpointInfo(["10.0.0.1"], false)] };
        var result = Render(b, WithBackends(new HttpBackendRef { Name = "web", Port = 8080 }));

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        Assert.AreEqual(0, result.Config.Clusters.Count);
        Assert.AreEqual("True", Cond(Parent(result).Conditions, "ResolvedRefs").Status);
    }

    [TestMethod]
    public void EmptyBackendShare_IsAnUndefinedClusterSoTheDataPlaneAnswersForIt()
    {
        var b = Basic();
        b.AddService("default", "down", 8080);
        var result = Render(b, WithBackends(
            new HttpBackendRef { Name = "web", Port = 8080, Weight = 3 },
            new HttpBackendRef { Name = "down", Port = 8080, Weight = 1 }));

        var route = result.Config.UserRoutes().Single();
        Assert.IsNull(route.DirectResponse);
        Assert.AreEqual(2, route.Clusters!.Count);
        Assert.AreEqual(1, route.Clusters[1].Weight);
        Assert.IsFalse(result.Config.Clusters.Any(c => c.Id == route.Clusters[1].ClusterId));
    }

    [TestMethod]
    public void MissingService_Is500AndBackendNotFound()
    {
        var b = Basic();
        var result = Render(b, WithBackends(new HttpBackendRef { Name = "ghost", Port = 8080 }));

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        var refs = Cond(Parent(result).Conditions, "ResolvedRefs");
        Assert.AreEqual("False", refs.Status);
        Assert.AreEqual("BackendNotFound", refs.Reason);
        Assert.AreEqual("True", Cond(Parent(result).Conditions, "Accepted").Status);
    }

    [TestMethod]
    public void WrongKindAndWrongPort_AreRejected()
    {
        var b = Basic();
        var kind = Render(b, WithBackends(new HttpBackendRef { Name = "web", Port = 8080, Kind = "Foo", Group = "x.io" }));
        Assert.AreEqual("InvalidKind", Cond(Parent(kind).Conditions, "ResolvedRefs").Reason);

        var b2 = Basic();
        var port = Render(b2, WithBackends(new HttpBackendRef { Name = "web", Port = 1 }));
        Assert.AreEqual("BackendNotFound", Cond(Parent(port).Conditions, "ResolvedRefs").Reason);
    }

    [TestMethod]
    public void CrossNamespaceBackend_NeedsAReferenceGrant()
    {
        var b = Basic();
        b.AddService("data", "db", 8080, "10.9.0.1");
        var denied = Render(b, WithBackends(new HttpBackendRef { Name = "db", Port = 8080, Namespace = "data" }));
        Assert.AreEqual("RefNotPermitted", Cond(Parent(denied).Conditions, "ResolvedRefs").Reason);
        Assert.AreEqual(500, denied.Config.UserRoutes().Single().DirectResponse?.Status);

        b.Routes.Clear();
        b.Grants.Add(new ReferenceGrant
        {
            Metadata = new ObjectMeta { Name = "g", Namespace = "data" },
            Spec = new ReferenceGrantSpec
            {
                From = [new ReferenceGrantFrom { Group = GatewayApiNames.Group, Kind = "HTTPRoute", Namespace = "default" }],
                To = [new ReferenceGrantTo { Group = string.Empty, Kind = "Service", Name = "db" }],
            },
        });
        var allowed = Render(b, WithBackends(new HttpBackendRef { Name = "db", Port = 8080, Namespace = "data" }));
        Assert.AreEqual("data/db:8080", allowed.Config.UserRoutes().Single().ClusterId);
        Assert.AreEqual("True", Cond(Parent(allowed).Conditions, "ResolvedRefs").Status);
    }

    [TestMethod]
    public void GrantForAnotherServiceName_DoesNotApply()
    {
        var b = Basic();
        b.AddService("data", "db", 8080, "10.9.0.1");
        b.Grants.Add(new ReferenceGrant
        {
            Metadata = new ObjectMeta { Name = "g", Namespace = "data" },
            Spec = new ReferenceGrantSpec
            {
                From = [new ReferenceGrantFrom { Group = GatewayApiNames.Group, Kind = "HTTPRoute", Namespace = "default" }],
                To = [new ReferenceGrantTo { Group = string.Empty, Kind = "Service", Name = "other" }],
            },
        });
        var result = Render(b, WithBackends(new HttpBackendRef { Name = "db", Port = 8080, Namespace = "data" }));

        Assert.AreEqual("RefNotPermitted", Cond(Parent(result).Conditions, "ResolvedRefs").Reason);
    }

    [TestMethod]
    public void RuleWithoutBackends_Answers500()
    {
        var b = Basic();
        var result = Render(b, Route("r", "default", null, new HttpRouteRule()));

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void HeaderModifiers_BecomeTransforms()
    {
        var b = Basic();
        var rule = Rule("web");
        rule.Filters =
        [
            new HttpRouteFilter
            {
                Type = "RequestHeaderModifier",
                RequestHeaderModifier = new HttpHeaderFilter
                {
                    Set = [new HttpHeader { Name = "X-Set", Value = "1" }],
                    Add = [new HttpHeader { Name = "X-Add", Value = "2" }],
                    Remove = ["X-Remove"],
                },
            },
            new HttpRouteFilter
            {
                Type = "ResponseHeaderModifier",
                ResponseHeaderModifier = new HttpHeaderFilter
                {
                    Set = [new HttpHeader { Name = "X-Out", Value = "3" }],
                    Remove = ["Server"],
                },
            },
        ];
        var kinds = Render(b, Route("r", "default", null, rule)).Config.UserRoutes().Single().Transforms!.Select(t => t.Kind).ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                TransformKinds.RequestHeaderRemove,
                TransformKinds.RequestHeaderSet,
                TransformKinds.RequestHeaderAppend,
                TransformKinds.ResponseHeaderRemove,
                TransformKinds.ResponseHeaderSet,
            },
            kinds);
    }

    [TestMethod]
    public void UrlRewrite_ReplacesTheFullPathAndKeepsTheQuery()
    {
        var b = Basic();
        var rule = Rule("web", 8080, PathPrefix("/old"));
        rule.Filters =
        [
            new HttpRouteFilter
            {
                Type = "URLRewrite",
                UrlRewrite = new HttpUrlRewrite
                {
                    Hostname = "internal.test",
                    Path = new HttpPathModifier { Type = "ReplaceFullPath", ReplaceFullPath = "/new$" },
                },
            },
        ];
        var transforms = Render(b, Route("r", "default", null, rule)).Config.UserRoutes().Single().Transforms!;

        Assert.AreEqual("Host", transforms[0].Parameters!["name"]);
        Assert.AreEqual("internal.test", transforms[0].Parameters!["value"]);
        Assert.AreEqual(TransformKinds.PathRegexReplace, transforms[1].Kind);
        Assert.AreEqual("/new$$", transforms[1].Parameters!["replacement"]);

        var ctx = new TransformRequestContext { Path = "/old/x?a=1" };
        new Titanium.Web.Proxy.Transforms.TransformEngine().ApplyRequestTransforms(transforms, ctx);
        Assert.AreEqual("/new$?a=1", ctx.Path);
    }

    [TestMethod]
    [DataRow("/abc", "/abc/def?x=1", "/xyz/def?x=1")]
    [DataRow("/abc", "/abc", "/xyz")]
    [DataRow("/abc", "/abc?x=1", "/xyz?x=1")]
    [DataRow("/abc", "/abc/", "/xyz/")]
    public void UrlRewrite_ReplacePrefixMatch_FollowsTheSpecTable(string prefix, string input, string expected)
    {
        var b = Basic();
        var rule = Rule("web", 8080, PathPrefix(prefix));
        rule.Filters =
        [
            new HttpRouteFilter
            {
                Type = "URLRewrite",
                UrlRewrite = new HttpUrlRewrite { Path = new HttpPathModifier { Type = "ReplacePrefixMatch", ReplacePrefixMatch = "/xyz" } },
            },
        ];
        var transforms = Render(b, Route("r", "default", null, rule)).Config.UserRoutes().Single().Transforms!;

        var ctx = new TransformRequestContext { Path = input };
        new Titanium.Web.Proxy.Transforms.TransformEngine().ApplyRequestTransforms(transforms, ctx);
        Assert.AreEqual(expected, ctx.Path);
    }

    [TestMethod]
    public void UrlRewrite_ReplacePrefixMatchWithSlash_DropsThePrefix()
    {
        var b = Basic();
        var rule = Rule("web", 8080, PathPrefix("/abc"));
        rule.Filters =
        [
            new HttpRouteFilter
            {
                Type = "URLRewrite",
                UrlRewrite = new HttpUrlRewrite { Path = new HttpPathModifier { Type = "ReplacePrefixMatch", ReplacePrefixMatch = "/" } },
            },
        ];
        var transforms = Render(b, Route("r", "default", null, rule)).Config.UserRoutes().Single().Transforms!;

        foreach (var (input, expected) in new[] { ("/abc/def", "/def"), ("/abc", "/"), ("/abc?q=1", "/?q=1") })
        {
            var ctx = new TransformRequestContext { Path = input };
            new Titanium.Web.Proxy.Transforms.TransformEngine().ApplyRequestTransforms(transforms, ctx);
            Assert.AreEqual(expected, ctx.Path, input);
        }
    }

    [TestMethod]
    public void Redirect_WithHostname_BecomesATerminalRedirect()
    {
        var b = Basic();
        var rule = new HttpRouteRule
        {
            Filters =
            [
                new HttpRouteFilter
                {
                    Type = "RequestRedirect",
                    RequestRedirect = new HttpRequestRedirect
                    {
                        Scheme = "https",
                        Hostname = "secure.example.com",
                        Port = 443,
                        StatusCode = 301,
                        Path = new HttpPathModifier { Type = "ReplaceFullPath", ReplaceFullPath = "/landing" },
                    },
                },
            ],
        };
        var route = Render(b, Route("r", "default", null, rule)).Config.UserRoutes().Single();

        Assert.AreEqual(301, route.Redirect!.Status);
        Assert.AreEqual("https", route.Redirect.Scheme);
        Assert.AreEqual("secure.example.com", route.Redirect.Host);
        Assert.IsNull(route.Redirect.Port, "the scheme's default port is left off");
        Assert.AreEqual("/landing", route.Redirect.Path);
        Assert.IsNull(route.DirectResponse);
    }

    [TestMethod]
    public void Redirect_WithoutHostname_IsRenderedOncePerConcreteHostname()
    {
        var b = Basic();
        var rule = new HttpRouteRule
        {
            Filters = [new HttpRouteFilter { Type = "RequestRedirect", RequestRedirect = new HttpRequestRedirect { Scheme = "https" } }],
        };
        var routes = Render(b, Route("r", "default", ["a.example.com", "b.example.com"], rule)).Config.UserRoutes();

        Assert.AreEqual(2, routes.Count);
        CollectionAssert.AreEquivalent(new[] { "a.example.com", "b.example.com" }, routes.Select(r => r.Redirect!.Host).ToArray());
        Assert.AreEqual(2, routes.Select(r => r.Id).Distinct().Count());
    }

    [TestMethod]
    public void Redirect_WithoutAnyHostname_IsUnsupported()
    {
        var b = Basic();
        var rule = new HttpRouteRule
        {
            Filters = [new HttpRouteFilter { Type = "RequestRedirect", RequestRedirect = new HttpRequestRedirect { Scheme = "https" } }],
        };
        var result = Render(b, Route("r", "default", null, rule));

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        Assert.AreEqual("True", Cond(Parent(result).Conditions, "PartiallyInvalid").Status);
    }

    [TestMethod]
    public void Redirect_ReplacePrefixMatch_IsUnsupported()
    {
        var b = Basic();
        var rule = new HttpRouteRule
        {
            Filters =
            [
                new HttpRouteFilter
                {
                    Type = "RequestRedirect",
                    RequestRedirect = new HttpRequestRedirect
                    {
                        Hostname = "x.example.com",
                        Path = new HttpPathModifier { Type = "ReplacePrefixMatch", ReplacePrefixMatch = "/a" },
                    },
                },
            ],
        };

        Assert.AreEqual(500, Render(b, Route("r", "default", null, rule)).Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void Mirror_BecomesAMirrorClusterWithItsPercent()
    {
        var b = Basic();
        b.AddService("default", "shadow", 8080, "10.7.0.1");
        var rule = Rule("web");
        rule.Filters =
        [
            new HttpRouteFilter
            {
                Type = "RequestMirror",
                RequestMirror = new HttpRequestMirror
                {
                    BackendRef = new BackendObjectReference { Name = "shadow", Port = 8080 },
                    Fraction = new MirrorFraction { Numerator = 1, Denominator = 4 },
                },
            },
        ];
        var result = Render(b, Route("r", "default", null, rule));

        var mirror = result.Config.UserRoutes().Single().Mirror!;
        Assert.AreEqual("default/shadow:8080", mirror.ClusterId);
        Assert.AreEqual(25, mirror.SamplePercent);
        Assert.AreEqual(2, result.Config.Clusters.Count);
    }

    [TestMethod]
    public void Mirror_ToAMissingBackend_IsAResolvedRefsFailure()
    {
        var b = Basic();
        var rule = Rule("web");
        rule.Filters =
        [
            new HttpRouteFilter
            {
                Type = "RequestMirror",
                RequestMirror = new HttpRequestMirror { BackendRef = new BackendObjectReference { Name = "ghost", Port = 8080 } },
            },
        ];
        var result = Render(b, Route("r", "default", null, rule));

        Assert.IsNull(result.Config.UserRoutes().Single().Mirror);
        Assert.AreEqual("False", Cond(Parent(result).Conditions, "ResolvedRefs").Status);
    }

    [TestMethod]
    public void ExtensionRefFilter_IsUnsupported()
    {
        var b = Basic();
        var rule = Rule("web");
        rule.Filters = [new HttpRouteFilter { Type = "ExtensionRef" }];

        Assert.AreEqual(500, Render(b, Route("r", "default", null, rule)).Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void Timeouts_MapToRouteLimitsAndABackendScopedCluster()
    {
        var b = Basic();
        var rule = Rule("web");
        rule.Timeouts = new HttpRouteTimeouts { Request = "2500ms", BackendRequest = "1s" };
        var result = Render(b, Route("r", "default", null, rule));

        var route = result.Config.UserRoutes().Single();
        Assert.AreEqual(3, route.Limits!.RequestTimeoutSeconds);
        Assert.AreEqual(TimeSpan.FromSeconds(1), result.Config.Clusters.Single().HttpClient!.RequestTimeout);
        Assert.IsTrue(route.ClusterId.Contains('@', StringComparison.Ordinal));
    }

    [TestMethod]
    public void ZeroTimeout_DisablesTheLimit_AndBadDurationIsUnsupported()
    {
        var b = Basic();
        var rule = Rule("web");
        rule.Timeouts = new HttpRouteTimeouts { Request = "0s" };
        Assert.IsNull(Render(b, Route("r", "default", null, rule)).Config.UserRoutes().Single().Limits);

        var b2 = Basic();
        var bad = Rule("web");
        bad.Timeouts = new HttpRouteTimeouts { Request = "soon" };
        Assert.AreEqual(500, Render(b2, Route("r", "default", null, bad)).Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void Config_RoundTripsThroughTheYamlLoaderTheDataPlaneUses()
    {
        var b = Basic();
        b.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));
        b.Gateways[0].Spec.Listeners.Add(Https("https", 443, "app.example.com", "cert"));
        var rule = Rule("web", 8080, PathPrefix("/api"), new HttpRouteMatch
        {
            Path = new HttpPathMatch { Type = "RegularExpression", Value = "/v[0-9]+" },
            QueryParams = [new HttpQueryParamMatch { Name = "v", Value = "2", Type = "RegularExpression" }],
            Headers = [new HttpHeaderMatch { Name = "X-A", Value = "b" }],
        });
        rule.Timeouts = new HttpRouteTimeouts { BackendRequest = "5s", Request = "10s" };
        rule.Filters =
        [
            new HttpRouteFilter { Type = "URLRewrite", UrlRewrite = new HttpUrlRewrite { Path = new HttpPathModifier { Type = "ReplacePrefixMatch", ReplacePrefixMatch = "/" } } },
        ];
        var result = Render(b, Route("r", "default", ["app.example.com"], rule));

        var json = TwpConfigLoader.ToJson(result.Config);
        var loaded = TwpConfigLoader.LoadYaml(json);

        Assert.AreEqual(result.Config.Routes.Count, loaded.Routes.Count);
        Assert.AreEqual(result.Config.Clusters.Count, loaded.Clusters.Count);
        Assert.AreEqual(0, TwpConfigValidator.Validate(loaded).Count, string.Join("; ", TwpConfigValidator.Validate(loaded)));
        Assert.AreEqual(result.Config.UserRoutes().Select(r => r.Match.PathKind).First(), loaded.Routes.Select(r => r.Match.PathKind).First());
        Assert.IsNotNull(loaded.Routes.First(r => r.Match.QueryParameters is not null).Match.QueryParameters);
        Assert.AreEqual(TimeSpan.FromSeconds(5), loaded.Clusters.First(c => c.HttpClient is not null).HttpClient!.RequestTimeout);
        Assert.AreEqual("TITANIUM_ADMIN_TOKEN", loaded.Server!.Admin!.AuthTokenEnv);
        Assert.AreEqual("transparent", loaded.Listeners[0].Type);
    }
}
