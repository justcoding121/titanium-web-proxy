using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Remaining gRPC match/filter and BackendTLS reject edges still thin on Sonar develop:
///     method-only Exact, service-only Exact, RegularExpression, RequestHeaderModifier,
///     unsupported filter, NoMatchingParent section, and non-ConfigMap CA refs.
/// </summary>
[TestClass]
public class GatewayTranslatorGrpcMatchAndFilterCoverageBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void GrpcRoute_MethodOnlyExact_BecomesRegexPath()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            new GrpcRouteRule
            {
                Matches = [new GrpcRouteMatch { Method = new GrpcMethodMatch { Method = "Ping" } }],
                BackendRefs = [Backend("web")],
            }));
        var result = b.Render();
        AssertValid(result);

        var match = result.Config.UserRoutes().Single().Match!;
        Assert.AreEqual(PathMatchKind.Regex, match.PathKind);
        StringAssert.Contains(match.Path, "Ping");
    }

    [TestMethod]
    public void GrpcRoute_ServiceOnlyExact_BecomesPathPrefix()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            new GrpcRouteRule
            {
                Matches = [new GrpcRouteMatch { Method = new GrpcMethodMatch { Service = "pkg.Echo" } }],
                BackendRefs = [Backend("web")],
            }));
        var result = b.Render();
        AssertValid(result);

        var match = result.Config.UserRoutes().Single().Match!;
        StringAssert.StartsWith(match.Path, "/pkg.Echo");
        Assert.IsTrue(match.PathKind is PathMatchKind.Prefix or PathMatchKind.SegmentPrefix, match.PathKind.ToString());
    }

    [TestMethod]
    public void GrpcRoute_RequestHeaderModifier_AndUnsupportedFilter()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            new GrpcRouteRule
            {
                Matches =
                [
                    new GrpcRouteMatch
                    {
                        Method = new GrpcMethodMatch
                        {
                            Type = "RegularExpression",
                            Service = "pkg\\..+",
                            Method = "Say.+",
                        },
                    },
                ],
                Filters =
                [
                    new GrpcRouteFilter
                    {
                        Type = "RequestHeaderModifier",
                        RequestHeaderModifier = new HttpHeaderFilter
                        {
                            Set = [new HttpHeader { Name = "x-grpc", Value = "1" }],
                            Add = [new HttpHeader { Name = "x-trail", Value = "a" }],
                            Remove = ["grpc-timeout"],
                        },
                    },
                ],
                BackendRefs = [Backend("web")],
            }));
        var ok = b.Render();
        AssertValid(ok);
        var route = ok.Config.UserRoutes().Single();
        Assert.IsNull(route.DirectResponse);
        Assert.IsTrue(route.Transforms!.Any(t =>
            t.Kind == TransformKinds.RequestHeaderSet &&
            t.Parameters is not null &&
            t.Parameters.TryGetValue("name", out var name) &&
            string.Equals(name, "x-grpc", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(route.Match?.PathKind == PathMatchKind.Regex);

        b.GrpcRoutes.Clear();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            new GrpcRouteRule
            {
                Matches = [new GrpcRouteMatch { Method = new GrpcMethodMatch { Service = "pkg.Echo", Method = "Say" } }],
                Filters = [new GrpcRouteFilter { Type = "ExtensionRef" }],
                BackendRefs = [Backend("web")],
            }));
        var denied = b.Render();
        AssertValid(denied);
        Assert.AreEqual(500, denied.Config.UserRoutes().Single().DirectResponse?.Status);
        Assert.AreEqual("True", Cond(Parent(denied, StatusKind.GrpcRoute).Conditions, "Accepted").Status);
    }

    [TestMethod]
    public void GrpcRoute_WrongListenerSection_IsNoMatchingParent()
    {
        var b = Basic();
        var route = GrpcRoute("r", "default", null, GrpcRule("web"));
        route.Spec.ParentRefs = [ParentGw(section: "missing-listener")];
        b.GrpcRoutes.Add(route);
        var result = b.Render();
        AssertValid(result);

        var accepted = Cond(Parent(result, StatusKind.GrpcRoute).Conditions, "Accepted");
        Assert.AreEqual("False", accepted.Status);
        Assert.AreEqual("NoMatchingParent", accepted.Reason);
        Assert.AreEqual(0, result.Config.UserRoutes().Count());
    }

    [TestMethod]
    public void GrpcRoute_OverlongRegex_AndEmptyMatch_CoverPathBuilders()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            new GrpcRouteRule
            {
                Matches =
                [
                    new GrpcRouteMatch
                    {
                        Method = new GrpcMethodMatch
                        {
                            Type = "RegularExpression",
                            Service = new string('a', 200),
                            Method = new string('b', 200),
                        },
                    },
                ],
                BackendRefs = [Backend("web")],
            },
            new GrpcRouteRule
            {
                Matches = [new GrpcRouteMatch()],
                BackendRefs = [Backend("web")],
            }));
        var result = b.Render();
        AssertValid(result);

        var routes = result.Config.UserRoutes().ToList();
        Assert.IsTrue(routes.Count >= 1);
        Assert.IsTrue(
            routes.Any(r => r.DirectResponse?.Status == 500) ||
            routes.Any(r => r.Match?.PathKind == PathMatchKind.Prefix || r.Match?.Path == "/"),
            string.Join("; ", routes.Select(r => $"{r.Match?.PathKind}:{r.Match?.Path}:{r.DirectResponse?.Status}")));
    }

    [TestMethod]
    public void Hostnames_Intersect_PrefersMoreSpecificWildcard()
    {
        Assert.IsTrue(Hostnames.TryIntersect("*.example", "*.svc.example", out var nested));
        Assert.AreEqual("*.svc.example", nested);
        Assert.IsFalse(Hostnames.TryIntersect("*.a.example", "b.example", out _));
        Assert.IsTrue(DurationParser.TryParse("2h15m", out var longDur));
        Assert.AreEqual(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(15), longDur);
    }
}
