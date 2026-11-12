using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Remaining Listener / L4 leftovers still thin on Sonar develop: TCP hostname reject,
///     TLS mode neither Passthrough nor Terminate, AllowedRoutes kind filter, and TCP
///     NoMatchingParent by port.
/// </summary>
[TestClass]
public class GatewayListenerL4AndParsersCoverageBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void TcpListener_WithHostname_IsRejected()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener { Name = "tcp", Port = 80, Protocol = "TCP", Hostname = "bad.example" },
        ];
        b.TcpRoutes.Add(TcpRoute("r", "default", Backend("web")));
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "Accepted").Status);
        Assert.AreEqual("UnsupportedValue", Cond(listener.Conditions, "Accepted").Reason);
        Assert.AreEqual("False", Cond(Parent(result, StatusKind.TcpRoute).Conditions, "Accepted").Status);
    }

    [TestMethod]
    public void TlsListener_WithoutPassthroughOrTerminate_IsRejected()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener
            {
                Name = "tls",
                Port = 443,
                Protocol = "TLS",
                Tls = new ListenerTls { Mode = "TerminateAndPassthrough" },
            },
        ];
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "Accepted").Status);
        Assert.AreEqual("UnsupportedProtocol", Cond(listener.Conditions, "Accepted").Reason);
    }

    [TestMethod]
    public void HttpListener_AllowedRoutesKinds_PartialSupport_SetsInvalidRouteKinds()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners[0].AllowedRoutes = new AllowedRoutes
        {
            Kinds =
            [
                new RouteGroupKind { Group = GatewayApiNames.Group, Kind = "HTTPRoute" },
                new RouteGroupKind { Group = "example.com", Kind = "Widget" },
            ],
        };
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "ResolvedRefs").Status);
        Assert.AreEqual("InvalidRouteKinds", Cond(listener.Conditions, "ResolvedRefs").Reason);
        Assert.AreEqual("True", Cond(Parent(result, StatusKind.HttpRoute).Conditions, "Accepted").Status);
    }

    [TestMethod]
    public void TcpRoute_ParentPortMismatch_IsNoMatchingParent()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        var route = TcpRoute("r", "default", Backend("web"));
        route.Spec.ParentRefs = [new ParentReference { Name = "gw", Port = 9999 }];
        b.TcpRoutes.Add(route);
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("False", Cond(Parent(result, StatusKind.TcpRoute).Conditions, "Accepted").Status);
        Assert.AreEqual("NoMatchingParent", Cond(Parent(result, StatusKind.TcpRoute).Conditions, "Accepted").Reason);
    }

    [TestMethod]
    public void UdpListener_WithHostname_IsRejected()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener { Name = "udp", Port = 53, Protocol = "UDP", Hostname = "dns.example" },
        ];
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "Accepted").Status);
        Assert.AreEqual("UnsupportedValue", Cond(listener.Conditions, "Accepted").Reason);
    }
}
