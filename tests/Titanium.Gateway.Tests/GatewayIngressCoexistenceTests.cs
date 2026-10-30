using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Routing;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>A Gateway API route and an Ingress in the same cluster share one data-plane config without colliding.</summary>
[TestClass]
public class GatewayIngressCoexistenceTests
{
    private static StateBuilder Both(string ingressHost, string routeHost = "gw.test", DateTimeOffset? ingressCreated = null)
    {
        var b = Basic();
        b.IngressClasses.Add(IngressClass("titanium"));
        b.AddService("default", "shop", 8080, "10.0.5.1");
        b.Routes.Add(Route("web", "default", [routeHost], Rule("web")));
        b.Ingresses.Add(Ingress("shop", "titanium", [IRule(ingressHost, IPath("/", "Prefix", "shop"))], created: ingressCreated));
        return b;
    }

    private static RenderResult Render(StateBuilder b)
    {
        var result = b.Render();
        var errors = TwpConfigValidator.Validate(result.Config);
        Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
        return result;
    }

    private static string? Winner(RenderResult result, string host, string path = "/") =>
        new RouteMatcher().Match(new RouteMatchContext(host, path, "GET", null, null), result.Config.Routes.ToList())?.Id;

    [TestMethod]
    public void BothKindsAreServed_WithDistinctRouteIdsAndOrders()
    {
        var result = Render(Both("ing.test"));

        var routes = result.Config.UserRoutes();
        Assert.AreEqual(2, routes.Count);
        Assert.AreEqual(routes.Count, routes.Select(r => r.Id).Distinct().Count(), "route ids");
        Assert.AreEqual(routes.Count, routes.Select(r => r.Order).Distinct().Count(), "route orders");
        Assert.AreEqual(1, routes.Count(r => r.Id.StartsWith("ingress/", StringComparison.Ordinal)));
        Assert.AreEqual("default/shop:8080", result.Config.Routes.Single(r => r.Id == Winner(result, "ing.test")).ClusterId);
        Assert.AreEqual("default/web:8080", result.Config.Routes.Single(r => r.Id == Winner(result, "gw.test")).ClusterId);
        Assert.AreEqual(GatewayTranslator.NoMatchRouteId, Winner(result, "nobody.test"));
        Assert.AreEqual(GatewayTranslator.NoMatchRouteId, result.Config.Routes[^1].Id);
    }

    [TestMethod]
    public void BothKindsShareTheHttpListenerAndTheClusterOfASharedService()
    {
        var b = Both("ing.test");
        b.Ingresses.Add(Ingress("web", "titanium", [IRule("other.test", IPath("/", "Prefix"))]));

        var result = Render(b);

        Assert.AreEqual(1, result.Config.Listeners.Count);
        Assert.AreEqual(8080, result.Config.Listeners.Single().Port);
        Assert.AreEqual(1, result.Config.Clusters.Count(c => c.Id == "default/web:8080"));
        Assert.AreEqual(2, result.Config.UserRoutes().Count(r => r.ClusterId == "default/web:8080"));
    }

    [TestMethod]
    public void TheSameHostAndPath_GoesToWhicheverWasCreatedFirst_AcrossBothKinds()
    {
        var ingressFirst = Render(Both("same.test", "same.test", T0.AddHours(-1)));
        var routeFirst = Render(Both("same.test", "same.test", T0.AddHours(1)));

        Assert.AreEqual("default/shop:8080", ingressFirst.Config.Routes.Single(r => r.Id == Winner(ingressFirst, "same.test")).ClusterId);
        Assert.AreEqual("default/web:8080", routeFirst.Config.Routes.Single(r => r.Id == Winner(routeFirst, "same.test")).ClusterId);
        Assert.AreEqual(2, ingressFirst.Config.UserRoutes().Count, "neither is dropped");
    }

    [TestMethod]
    public void AnIngressAnnotationNeverTouchesAGatewayRoute()
    {
        var b = Both("ing.test");
        b.Ingresses.Clear();
        b.Ingresses.Add(Ingress("shop", "titanium", [IRule("ing.test", IPath("/", "Prefix", "web"))], new() { [IngressAnnotations.Prefix + "limit-rps"] = "5" }));

        var result = Render(b);

        var route = result.Config.UserRoutes().Single(r => r.Id.StartsWith("ingress/", StringComparison.Ordinal));
        Assert.AreEqual(1, route.Policies!.Count);
        Assert.IsNull(result.Config.UserRoutes().Single(r => !r.Id.StartsWith("ingress/", StringComparison.Ordinal)).Policies);
    }

    [TestMethod]
    public void AnIngressWithoutAnOwnedClass_LeavesTheGatewayConfigUntouched()
    {
        var gatewayOnly = Basic();
        gatewayOnly.Routes.Add(Route("web", "default", ["gw.test"], Rule("web")));
        var before = TwpConfigLoader.ToJson(gatewayOnly.Render().Config);

        gatewayOnly.IngressClasses.Add(IngressClass("other-class", controller: "example.com/other"));
        gatewayOnly.Ingresses.Add(Ingress("foreign", "other-class", [IRule("x.test", IPath("/", "Prefix"))]));

        Assert.AreEqual(before, TwpConfigLoader.ToJson(gatewayOnly.Render().Config));
    }

    [TestMethod]
    public void AnHttpsGatewayListenerOnTheIngressHttpPort_IsAProtocolClash_ReportedOnTheIngress()
    {
        var b = Basic();
        b.Gateways.Clear();
        b.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));
        b.Gateways.Add(StateBuilder.Gateway("gw", "default", [Https("https", 80, null, "cert")]));
        b.IngressClasses.Add(IngressClass("titanium"));
        b.Routes.Add(Route("web", "default", ["gw.test"], Rule("web")));
        b.Ingresses.Add(Ingress("shop", "titanium", [IRule("ing.test", IPath("/", "Prefix"))]));

        var result = b.Render();

        Assert.IsTrue(result.Events.Any(e => e.Reason == "PortUnavailable" && e.Name == "shop"));
        Assert.IsFalse(result.Config.UserRoutes().Any(r => r.Id.StartsWith("ingress/", StringComparison.Ordinal)));
        Assert.IsTrue(result.Config.UserRoutes().Any(r => !r.Id.StartsWith("ingress/", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TlsFromBothKinds_ProducesOneFilePerSecret_AndBothBindings()
    {
        var b = Basic();
        b.Gateways.Clear();
        b.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));
        b.Gateways.Add(StateBuilder.Gateway("gw", "default", [Https("https", 443, "gw.test", "cert")]));
        b.IngressClasses.Add(IngressClass("titanium"));
        b.Routes.Add(Route("web", "default", ["gw.test"], Rule("web")));
        var ingress = Ingress("shop", "titanium", [IRule("ing.test", IPath("/", "Prefix"))]);
        ingress.Spec.Tls = [new IngressTls { Hosts = ["ing.test"], SecretName = "cert" }];
        b.Ingresses.Add(ingress);

        var result = Render(b);

        Assert.AreEqual(1, result.TlsFiles.Count(f => f.FileName == "default.cert.pfx"), "the same Secret is converted once");
        var items = result.Config.Certificates!.Items;
        var item = items.Single();
        Assert.IsTrue(item.Hosts.Contains("gw.test"));
        Assert.IsTrue(item.Hosts.Contains("ing.test"));
        Assert.AreEqual(1, result.Config.Listeners.Count(l => l.DecryptSsl));
        Assert.AreEqual(8443, result.Config.Listeners.Single(l => l.DecryptSsl).Port);
    }
}
