using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Listener / gRPC leftovers still thin on develop Sonar: UDP hostname reject, unmapped port,
///     TCP AllowedRoutes From=All, gRPC hostname intersection, and wrong-namespace grants.
/// </summary>
[TestClass]
public class GatewayListenerHttpsUdpAndGrpcHostBehaviorTests
{
    private static readonly string[] ExpectedMatchHosts = ["app.example.com"];

    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void UdpListener_WithHostname_IsRejected()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener { Name = "udp", Port = 53, Protocol = "UDP", Hostname = "dns.example" },
        ];
        b.AddUdpService("default", "dns", 53, "10.0.0.9");
        b.UdpRoutes.Add(UdpRoute("r", "default", Backend("dns", 53)));
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "Accepted").Status);
        Assert.AreEqual("UnsupportedValue", Cond(listener.Conditions, "Accepted").Reason);
    }

    [TestMethod]
    public void HttpListener_UnmappedPort_IsPortUnavailable()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Http("http", 9999)];
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "Accepted").Status);
        Assert.AreEqual("PortUnavailable", Cond(listener.Conditions, "Accepted").Reason);
        Assert.AreEqual(0, result.Config.UserRoutes().Count);
    }

    [TestMethod]
    public void TcpRoute_AllowedRoutesFromAll_AdmitsOtherNamespace()
    {
        var b = Basic();
        b.Namespaces.Add(new NamespaceInfo("apps", new Dictionary<string, string>()));
        b.AddService("apps", "web", 8080, "10.2.0.1");
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener
            {
                Name = "tcp",
                Port = 80,
                Protocol = "TCP",
                AllowedRoutes = new AllowedRoutes { Namespaces = new RouteNamespaces { From = "All" } },
            },
        ];
        b.TcpRoutes.Add(TcpRoute("r", "apps", Backend("web")));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual(
            "True",
            Cond(result.StatusPatches.Single(p => p.Kind == StatusKind.TcpRoute).Parents!.Single().Conditions, "Accepted").Status);
        Assert.AreEqual("apps/web:8080", result.Config.Listeners.Single(l => l.Passthrough).SniDefaultClusterId);
    }

    [TestMethod]
    public void GrpcRoute_Hostnames_IntersectAndRejectNoOverlap()
    {
        var ok = Basic();
        ok.Gateways[0].Spec.Listeners[0].Hostname = "*.example.com";
        ok.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            ["app.example.com", "other.org"],
            GrpcRule("web")));
        var okResult = ok.Render();
        AssertValid(okResult);
        CollectionAssert.AreEqual(
            ExpectedMatchHosts,
            okResult.Config.UserRoutes().Single().Match!.Hosts!.ToArray());

        var deny = Basic();
        deny.Gateways[0].Spec.Listeners[0].Hostname = "a.example.com";
        deny.GrpcRoutes.Add(GrpcRoute("r", "default", ["b.example.com"], GrpcRule("web")));
        var denyResult = deny.Render();
        AssertValid(denyResult);
        Assert.AreEqual(
            "NotAllowedByListeners",
            Cond(Parent(denyResult, StatusKind.GrpcRoute).Conditions, "Accepted").Reason);
        Assert.AreEqual(0, denyResult.Config.UserRoutes().Count);
    }

    [TestMethod]
    public void GrantIndex_SkipsGrantsInWrongNamespace()
    {
        // Bug class: a grant living in the wrong namespace must not authorize a cross-ns Secret.
        var grants = new List<ReferenceGrant>
        {
            new()
            {
                Metadata = new ObjectMeta { Name = "wrong-ns", Namespace = "other" },
                Spec = new ReferenceGrantSpec
                {
                    From =
                    [
                        new ReferenceGrantFrom
                        {
                            Group = GatewayApiNames.Group,
                            Kind = GatewayApiNames.GatewayKind,
                            Namespace = "default",
                        },
                    ],
                    To =
                    [
                        new ReferenceGrantTo { Group = string.Empty, Kind = GatewayApiNames.SecretKind },
                    ],
                },
            },
        };
        var index = new GrantIndex(grants);
        Assert.IsFalse(index.Allows(
            GatewayApiNames.Group,
            GatewayApiNames.GatewayKind,
            "default",
            string.Empty,
            GatewayApiNames.SecretKind,
            "certs",
            "tls"));
    }
}
