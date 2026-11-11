using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     gRPC parent attach and match leftovers that mirror HTTP Route tests: port/section NoMatchingParent,
///     RegularExpression wildcards, service-only Exact, multi-backend primary, and empty CA refs for BackendTLS.
/// </summary>
[TestClass]
public class GatewayTranslatorGrpcParentAndMatchBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind = StatusKind.GrpcRoute, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void GrpcRoute_ParentRefPort_NoMatchingParent()
    {
        var b = Basic();
        var route = GrpcRoute("r", "default", null, GrpcRule("web"));
        route.Spec.ParentRefs = [new ParentReference { Name = "gw", Port = 9999 }];
        b.GrpcRoutes.Add(route);
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("False", Cond(Parent(result).Conditions, "Accepted").Status);
        Assert.AreEqual("NoMatchingParent", Cond(Parent(result).Conditions, "Accepted").Reason);
        Assert.AreEqual(0, result.Config.UserRoutes().Count);
    }

    [TestMethod]
    public void GrpcRoute_RegexMethod_NullServiceAndMethod_UsesWildcardPath()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            new GrpcRouteRule
            {
                BackendRefs = [Backend("web")],
                Matches = [new GrpcRouteMatch { Method = new GrpcMethodMatch { Type = "RegularExpression" } }],
            }));
        var result = b.Render();
        AssertValid(result);

        var match = result.Config.UserRoutes().Single().Match!;
        Assert.AreEqual(PathMatchKind.Regex, match.PathKind);
        StringAssert.Contains(match.Path, "^/[^/]+/[^/]+$");
    }

    [TestMethod]
    public void GrpcRoute_TwoUsableBackends_PrimaryIsFirstOnly()
    {
        // Bug class: weighted gRPC backends must not silently drop the first usable cluster when
        // PlanGrpcBackends only assigns PrimaryCluster = usable[0].
        var b = Basic();
        b.AddService("default", "alt", 8080, "10.9.0.1");
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
                        BackendRefs =
                        [
                            new HttpBackendRef { Name = "web", Port = 8080, Weight = 10 },
                            new HttpBackendRef { Name = "alt", Port = 8080, Weight = 90 },
                        ],
                        Matches = [new GrpcRouteMatch { Method = new GrpcMethodMatch { Service = "pkg.Echo", Method = "Say" } }],
                    },
                ],
            },
        });
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("default/web:8080", result.Config.UserRoutes().Single().ClusterId);
    }

    [TestMethod]
    public void BackendTls_EmptyCaRefs_LeavesClusterWithoutHttpClient()
    {
        // Bug class: hostname with non-System CA and empty CaCertificateRefs must not invent a half-TLS cluster.
        var b = Basic();
        b.BackendTlsPolicies.Add(BackendTls(
            "pol",
            "default",
            "web",
            "web.svc.cluster.local",
            caConfigMaps: []));
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render(BackendTlsOptions);
        AssertValid(result);

        Assert.IsNull(result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
        Assert.AreEqual(0, result.InlineTlsFiles.Count);
    }
}
