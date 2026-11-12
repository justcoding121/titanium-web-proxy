using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

[TestClass]
public class GatewayTranslatorL4Tests
{
    private static readonly string[] ExpectedClusterAddresses = ["10.0.0.1", "10.0.0.2"];
    private static readonly string[] AppExampleHost = ["app.example.com"];

    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    private static ListenerConfig? L4Listener(RenderResult result, int port) =>
        result.Config.Listeners.SingleOrDefault(l => l.Port == port && (l.Passthrough || string.Equals(l.Type, "udp", StringComparison.OrdinalIgnoreCase)));

    [TestMethod]
    public void TlsRoute_AttachesNamedBackend_RecordsParent_AndPassesValidator()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [TlsPassthrough("tls", 443, "app.example.com")];
        b.TlsRoutes.Add(TlsRoute("r", "default", AppExampleHost, Backend("web")));
        var result = b.Render();
        AssertValid(result);

        var parent = Parent(result, StatusKind.TlsRoute);
        Assert.AreEqual("True", Cond(parent.Conditions, "Accepted").Status);
        Assert.AreEqual("Accepted", Cond(parent.Conditions, "Accepted").Reason);
        Assert.AreEqual("True", Cond(parent.Conditions, "ResolvedRefs").Status);
        Assert.AreEqual(Controller, parent.ControllerName);

        var gateway = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway);
        Assert.AreEqual(1, gateway.Listeners!.Single().AttachedRoutes);

        var listener = L4Listener(result, 8443);
        Assert.IsNotNull(listener);
        Assert.IsTrue(listener!.Passthrough);
        Assert.IsFalse(listener.DecryptSsl);
        Assert.AreEqual("default/web:8080", listener.SniRoutes.Single().ClusterId);
        Assert.AreEqual("app.example.com", listener.SniRoutes.Single().ServerName);
        Assert.IsNull(listener.SniDefaultClusterId);

        var cluster = result.Config.Clusters.Single(c => c.Id == "default/web:8080");
        CollectionAssert.AreEqual(ExpectedClusterAddresses, cluster.Destinations.Select(d => d.Address).ToArray());
    }

    [TestMethod]
    public void TcpRoute_AttachesNamedBackend_RecordsParent_AndPassesValidator()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        b.TcpRoutes.Add(TcpRoute("r", "default", Backend("web")));
        var result = b.Render();
        AssertValid(result);

        var parent = Parent(result, StatusKind.TcpRoute);
        Assert.AreEqual("True", Cond(parent.Conditions, "Accepted").Status);
        Assert.AreEqual("True", Cond(parent.Conditions, "ResolvedRefs").Status);
        Assert.AreEqual(1, result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single().AttachedRoutes);

        var listener = L4Listener(result, 8080);
        Assert.IsNotNull(listener);
        Assert.IsTrue(listener!.Passthrough);
        Assert.AreEqual("default/web:8080", listener.SniDefaultClusterId);
        Assert.AreEqual(0, listener.SniRoutes.Count);
        Assert.IsTrue(result.Config.Clusters.Any(c => c.Id == "default/web:8080"));
        Assert.AreEqual(1, result.Config.Listeners.Count(l => l.Port == 8080), "TCP must not also emit an HTTP-style listener on the same port");
    }

    [TestMethod]
    public void UdpRoute_AttachesNamedBackend_RecordsParent_AndPassesValidator()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Udp("udp", 53)];
        b.AddUdpService("default", "dns", 53, "10.0.0.1", "10.0.0.2");
        b.UdpRoutes.Add(UdpRoute("r", "default", Backend("dns", 53)));
        var result = b.Render();
        AssertValid(result);

        var parent = Parent(result, StatusKind.UdpRoute);
        Assert.AreEqual("True", Cond(parent.Conditions, "Accepted").Status);
        Assert.AreEqual("True", Cond(parent.Conditions, "ResolvedRefs").Status);
        Assert.AreEqual(1, result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single().AttachedRoutes);

        var listener = result.Config.Listeners.Single(l => string.Equals(l.Type, "udp", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(53, listener.Port);
        Assert.AreEqual("default/dns:53", listener.ClusterId);
        Assert.IsTrue(result.Config.Clusters.Any(c => c.Id == "default/dns:53"));
    }

    [TestMethod]
    [DataRow("TLS")]
    [DataRow("TCP")]
    [DataRow("UDP")]
    public void MissingBackend_IsBackendNotFound_AndInstallsNoListener(string kind)
    {
        var b = Basic();
        switch (kind)
        {
            case "TLS":
                b.Gateways[0].Spec.Listeners = [TlsPassthrough("tls", 443)];
                b.TlsRoutes.Add(TlsRoute("r", "default", null, Backend("ghost")));
                break;
            case "TCP":
                b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
                b.TcpRoutes.Add(TcpRoute("r", "default", Backend("ghost")));
                break;
            default:
                b.Gateways[0].Spec.Listeners = [Udp("udp", 53)];
                b.UdpRoutes.Add(UdpRoute("r", "default", Backend("ghost", 53)));
                break;
        }

        var result = b.Render();
        AssertValid(result);

        var statusKind = kind switch
        {
            "TLS" => StatusKind.TlsRoute,
            "TCP" => StatusKind.TcpRoute,
            _ => StatusKind.UdpRoute,
        };
        var parent = Parent(result, statusKind);
        Assert.AreEqual("True", Cond(parent.Conditions, "Accepted").Status);
        Assert.AreEqual("False", Cond(parent.Conditions, "ResolvedRefs").Status);
        Assert.AreEqual("BackendNotFound", Cond(parent.Conditions, "ResolvedRefs").Reason);
        Assert.AreEqual(0, result.Config.Listeners.Count, "a missing backend must not install an L4 listener");
        Assert.AreEqual(0, result.Config.Clusters.Count);
    }

    [TestMethod]
    public void ParentTheGatewayDoesNotOwn_IsIgnored_WithoutStealingAnotherRoutesPort()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        b.TcpRoutes.Add(TcpRoute("owner", "default", Backend("web")));

        var foreign = TcpRoute("foreign", "default", Backend("web"));
        foreign.Spec.ParentRefs = [ParentGw("other-gw")];
        b.TcpRoutes.Add(foreign);

        var result = b.Render();
        AssertValid(result);

        Assert.IsFalse(result.StatusPatches.Any(p => p.Kind == StatusKind.TcpRoute && p.Name == "foreign"));
        var owner = Parent(result, StatusKind.TcpRoute, "owner");
        Assert.AreEqual("True", Cond(owner.Conditions, "Accepted").Status);
        Assert.AreEqual("default/web:8080", L4Listener(result, 8080)!.SniDefaultClusterId);
        Assert.AreEqual(1, result.Config.Listeners.Count);
    }

    [TestMethod]
    public void SecondTcpRoute_OnSameListener_IsRejected_WithoutStealingThePort()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        var first = TcpRoute("first", "default", Backend("web"));
        first.Metadata.CreationTimestamp = T0;
        var second = TcpRoute("second", "default", Backend("web"));
        second.Metadata.CreationTimestamp = T0.AddMinutes(1);
        b.TcpRoutes.Add(first);
        b.TcpRoutes.Add(second);
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("True", Cond(Parent(result, StatusKind.TcpRoute, "first").Conditions, "Accepted").Status);
        Assert.AreEqual("False", Cond(Parent(result, StatusKind.TcpRoute, "second").Conditions, "Accepted").Status);
        Assert.AreEqual("NotAllowedByListeners", Cond(Parent(result, StatusKind.TcpRoute, "second").Conditions, "Accepted").Reason);
        Assert.AreEqual("default/web:8080", L4Listener(result, 8080)!.SniDefaultClusterId);
        Assert.AreEqual(1, result.Config.Listeners.Count);
    }

    [TestMethod]
    public void GrpcRoute_ExactMethod_MapsToHttpPathAndCluster()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            GrpcRule("web", 8080, new GrpcRouteMatch
            {
                Method = new GrpcMethodMatch { Service = "pkg.Echo", Method = "Say" },
            })));
        var result = b.Render();
        AssertValid(result);

        var parent = Parent(result, StatusKind.GrpcRoute);
        Assert.AreEqual("True", Cond(parent.Conditions, "Accepted").Status);
        Assert.AreEqual("True", Cond(parent.Conditions, "ResolvedRefs").Status);

        var route = result.Config.UserRoutes().Single();
        Assert.AreEqual("default/r/r0m0", route.Id);
        Assert.AreEqual("default/web:8080", route.ClusterId);
        Assert.AreEqual("/pkg.Echo/Say", route.Match.Path);
        Assert.AreEqual(PathMatchKind.Exact, route.Match.PathKind);
    }

    [TestMethod]
    public void GrpcRoute_UnknownMethodMatchType_IsUnsupportedAndAnswers500()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            GrpcRule("web", 8080, new GrpcRouteMatch
            {
                Method = new GrpcMethodMatch { Type = "Prefix", Service = "pkg.Echo", Method = "Say" },
            })));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        var invalid = Cond(Parent(result, StatusKind.GrpcRoute).Conditions, "PartiallyInvalid");
        Assert.AreEqual("True", invalid.Status);
        Assert.IsTrue(invalid.Message!.Contains("unsupported method match", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void GrpcRoute_MissingBackend_Is500AndBackendNotFound()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            new GrpcRouteRule
            {
                Matches = [new GrpcRouteMatch { Method = new GrpcMethodMatch { Service = "pkg.Echo", Method = "Say" } }],
                BackendRefs = [Backend("ghost")],
            }));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        var refs = Cond(Parent(result, StatusKind.GrpcRoute).Conditions, "ResolvedRefs");
        Assert.AreEqual("False", refs.Status);
        Assert.AreEqual("BackendNotFound", refs.Reason);
    }

    [TestMethod]
    public void GrpcRoute_ServiceOnly_MapsToSegmentPrefixForService()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            GrpcRule("web", 8080, new GrpcRouteMatch
            {
                Method = new GrpcMethodMatch { Service = "pkg.Echo" },
            })));
        var result = b.Render();
        AssertValid(result);

        var route = result.Config.UserRoutes().Single();
        // "/pkg.Echo/" is trimmed to "/pkg.Echo" by the PathPrefix handler.
        Assert.AreEqual("/pkg.Echo", route.Match.Path);
        Assert.AreEqual(PathMatchKind.SegmentPrefix, route.Match.PathKind);
    }

    [TestMethod]
    public void GrpcRoute_MethodOnly_MapsToRegex()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            GrpcRule("web", 8080, new GrpcRouteMatch
            {
                Method = new GrpcMethodMatch { Method = "Say" },
            })));
        var result = b.Render();
        AssertValid(result);

        var route = result.Config.UserRoutes().Single();
        Assert.IsNotNull(route.Match.Path);
        Assert.AreEqual(PathMatchKind.Regex, route.Match.PathKind);
        StringAssert.Contains(route.Match.Path, "Say");
    }

    [TestMethod]
    public void GrpcRoute_NoMethodMatch_MapsToRootPathPrefix()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            GrpcRule("web", 8080)));
        var result = b.Render();
        AssertValid(result);

        var route = result.Config.UserRoutes().Single();
        Assert.IsNull(route.Match.Path);
        Assert.AreEqual(PathMatchKind.Prefix, route.Match.PathKind);
    }

    [TestMethod]
    public void GrpcRoute_RegularExpressionType_MapsToRegex()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            GrpcRule("web", 8080, new GrpcRouteMatch
            {
                Method = new GrpcMethodMatch { Type = "RegularExpression", Service = "pkg\\..*", Method = "Say.*" },
            })));
        var result = b.Render();
        AssertValid(result);

        var route = result.Config.UserRoutes().Single();
        Assert.IsNotNull(route.Match.Path);
        Assert.AreEqual(PathMatchKind.Regex, route.Match.PathKind);
    }

    [TestMethod]
    public void AllZeroWeightBackends_GivesA500DirectResponse()
    {
        var b = Basic();
        var route = Route("r", "default", null, new HttpRouteRule
        {
            BackendRefs =
            [
                new HttpBackendRef { Name = "web", Port = 8080, Weight = 0 },
            ],
        });
        b.Routes.Add(route);
        var result = b.Render();

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        Assert.AreEqual(0, result.Config.Clusters.Count);
    }

    [TestMethod]
    public void UnsupportedPathType_IsDirectResponse500()
    {
        var b = Basic();
        var match = new HttpRouteMatch { Path = new HttpPathMatch { Type = "Glob", Value = "/api/**" } };
        var result = b.Render();
        b.Routes.Add(Route("r", "default", null, Rule("web", 8080, match)));
        var r = b.Render();

        Assert.AreEqual(500, r.Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void UnsupportedQueryParamMatchType_IsDirectResponse500()
    {
        var b = Basic();
        var match = new HttpRouteMatch
        {
            QueryParams = [new HttpQueryParamMatch { Name = "version", Value = "v1", Type = "Wildcard" }],
        };
        b.Routes.Add(Route("r", "default", null, Rule("web", 8080, match)));
        var result = b.Render();

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void URLRewrite_UnknownPathType_IsUnsupported()
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
                    Path = new HttpPathModifier { Type = "GrowLength" },
                },
            },
        ];
        b.Routes.Add(Route("r", "default", null, rule));
        var result = b.Render();

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void URLRewrite_ReplaceFullPath_NeedsAValue()
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
                    Path = new HttpPathModifier { Type = "ReplaceFullPath", ReplaceFullPath = string.Empty },
                },
            },
        ];
        b.Routes.Add(Route("r", "default", null, rule));
        var result = b.Render();

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void Redirect_NonDefaultPort_IsKeptOnUrl()
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
                        Port = 8443,
                        StatusCode = 302,
                    },
                },
            ],
        };
        b.Routes.Add(Route("r", "default", null, rule));
        var result = b.Render();

        var route = result.Config.UserRoutes().Single();
        Assert.AreEqual(8443, route.Redirect!.Port);
        Assert.AreEqual("https", route.Redirect.Scheme);
    }

    [TestMethod]
    public void Redirect_UnsupportedStatusCode_IsDirectResponse500()
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
                        StatusCode = 303,
                    },
                },
            ],
        };
        b.Routes.Add(Route("r", "default", null, rule));
        var result = b.Render();

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void Redirect_UnsupportedScheme_IsDirectResponse500()
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
                        Scheme = "ftp",
                    },
                },
            ],
        };
        b.Routes.Add(Route("r", "default", null, rule));
        var result = b.Render();

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void MirrorWithZeroPercent_IsSkipped()
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
                    Percent = 0,
                },
            },
        ];
        b.Routes.Add(Route("r", "default", null, rule));
        var result = b.Render();

        Assert.IsNull(result.Config.UserRoutes().Single().Mirror);
        // No ref error because zero-percent mirror is simply skipped.
        Assert.AreEqual("True", result.StatusPatches
            .Single(p => p.Kind == StatusKind.HttpRoute).Parents!.Single()
            .Conditions.Single(c => c.Type == "ResolvedRefs").Status);
    }

    [TestMethod]
    public void DuplicateFilterType_IsUnsupported()
    {
        var b = Basic();
        var rule = Rule("web", 8080);
        rule.Filters =
        [
            new HttpRouteFilter
            {
                Type = "URLRewrite",
                UrlRewrite = new HttpUrlRewrite { Hostname = "a.example.com" },
            },
            new HttpRouteFilter
            {
                Type = "URLRewrite",
                UrlRewrite = new HttpUrlRewrite { Hostname = "b.example.com" },
            },
        ];
        b.Routes.Add(Route("r", "default", null, rule));
        var result = b.Render();

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
    }
}
