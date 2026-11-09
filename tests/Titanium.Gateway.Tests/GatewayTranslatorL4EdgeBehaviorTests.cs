using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Extra L4/gRPC translator edges toward 90% line coverage.
///     Bug classes: default SNI owner, second UDP steal, gRPC filter/support limits, regex length.
/// </summary>
[TestClass]
public class GatewayTranslatorL4EdgeBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void TlsRoute_WithoutHostname_OwnsSniDefaultCluster()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [TlsPassthrough("tls", 443)];
        b.TlsRoutes.Add(TlsRoute("r", "default", null, Backend("web")));
        var result = b.Render();
        AssertValid(result);

        var listener = result.Config.Listeners.Single(l => l.Passthrough && l.Port == 8443);
        Assert.AreEqual("default/web:8080", listener.SniDefaultClusterId);
        Assert.AreEqual(0, listener.SniRoutes.Count);
    }

    [TestMethod]
    public void SecondUdpRoute_OnSameListener_IsRejected()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Udp("udp", 53)];
        b.AddUdpService("default", "dns", 53, "10.0.0.1");
        var first = UdpRoute("first", "default", Backend("dns", 53));
        first.Metadata.CreationTimestamp = T0;
        var second = UdpRoute("second", "default", Backend("dns", 53));
        second.Metadata.CreationTimestamp = T0.AddMinutes(1);
        b.UdpRoutes.Add(first);
        b.UdpRoutes.Add(second);
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("True", Cond(Parent(result, StatusKind.UdpRoute, "first").Conditions, "Accepted").Status);
        Assert.AreEqual("False", Cond(Parent(result, StatusKind.UdpRoute, "second").Conditions, "Accepted").Status);
        Assert.AreEqual("NotAllowedByListeners", Cond(Parent(result, StatusKind.UdpRoute, "second").Conditions, "Accepted").Reason);
        Assert.AreEqual(1, result.Config.Listeners.Count(l => string.Equals(l.Type, "udp", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void TlsRoute_EmptyBackendRefs_IsBackendNotFound()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [TlsPassthrough("tls", 443)];
        var route = TlsRoute("r", "default", null);
        route.Spec.Rules = [new L4RouteRule { BackendRefs = [] }];
        b.TlsRoutes.Add(route);
        var result = b.Render();
        AssertValid(result);

        var refs = Cond(Parent(result, StatusKind.TlsRoute).Conditions, "ResolvedRefs");
        Assert.AreEqual("False", refs.Status);
        Assert.AreEqual("BackendNotFound", refs.Reason);
        Assert.AreEqual(0, result.Config.Listeners.Count);
    }

    [TestMethod]
    public void GrpcRoute_RequestHeaderModifier_AppliesTransforms()
    {
        var b = Basic();
        b.GrpcRoutes.Add(new GrpcRoute
        {
            Metadata = new ObjectMeta { Name = "r", Namespace = "default", Generation = 1, CreationTimestamp = T0 },
            Spec = new GrpcRouteSpec
            {
                ParentRefs = [ParentGw()],
                Rules =
                [
                    new GrpcRouteRule
                    {
                        Filters =
                        [
                            new GrpcRouteFilter
                            {
                                Type = "RequestHeaderModifier",
                                RequestHeaderModifier = new HttpHeaderFilter
                                {
                                    Set = [new HttpHeader { Name = "x-trace", Value = "1" }],
                                },
                            },
                        ],
                        BackendRefs = [Backend("web")],
                        Matches = [new GrpcRouteMatch { Method = new GrpcMethodMatch { Service = "pkg.Echo", Method = "Say" } }],
                    },
                ],
            },
        });
        var result = b.Render();
        AssertValid(result);

        var route = result.Config.UserRoutes().Single();
        Assert.IsTrue(route.Transforms!.Any(t =>
            t.Kind == TransformKinds.RequestHeaderSet &&
            t.Parameters is not null &&
            t.Parameters.TryGetValue("name", out var name) &&
            string.Equals(name, "x-trace", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void GrpcRoute_UnsupportedFilter_Answers500()
    {
        var b = Basic();
        b.GrpcRoutes.Add(new GrpcRoute
        {
            Metadata = new ObjectMeta { Name = "r", Namespace = "default", Generation = 1, CreationTimestamp = T0 },
            Spec = new GrpcRouteSpec
            {
                ParentRefs = [ParentGw()],
                Rules =
                [
                    new GrpcRouteRule
                    {
                        Filters = [new GrpcRouteFilter { Type = "ExtensionRef" }],
                        BackendRefs = [Backend("web")],
                    },
                ],
            },
        });
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        var invalid = Cond(Parent(result, StatusKind.GrpcRoute).Conditions, "PartiallyInvalid");
        Assert.AreEqual("True", invalid.Status);
    }

    [TestMethod]
    public void GrpcRoute_RegexTooLong_IsUnsupported()
    {
        var b = Basic();
        var longService = new string('a', 200);
        var longMethod = new string('b', 200);
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            GrpcRule("web", 8080, new GrpcRouteMatch
            {
                Method = new GrpcMethodMatch
                {
                    Type = "RegularExpression",
                    Service = longService,
                    Method = longMethod,
                },
            })));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        var invalid = Cond(Parent(result, StatusKind.GrpcRoute).Conditions, "PartiallyInvalid");
        StringAssert.Contains(invalid.Message!, "too long");
    }

    [TestMethod]
    public void GrpcRoute_ForeignParent_IsIgnored()
    {
        var b = Basic();
        var route = GrpcRoute("r", "default", null, GrpcRule("web"));
        route.Spec.ParentRefs = [ParentGw("other-gw")];
        b.GrpcRoutes.Add(route);
        var result = b.Render();
        AssertValid(result);

        Assert.IsFalse(result.StatusPatches.Any(p => p.Kind == StatusKind.GrpcRoute));
        Assert.AreEqual(0, result.Config.UserRoutes().Count());
    }
}
