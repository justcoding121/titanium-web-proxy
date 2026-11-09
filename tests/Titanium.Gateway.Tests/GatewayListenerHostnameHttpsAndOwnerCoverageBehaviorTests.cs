using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft listener/L4 leftovers still thin on develop Sonar: TCP/UDP hostname reject, HTTPS
///     Passthrough / missing cert refs, partial InvalidRouteKinds, and dual TCP/UDP ownership.
/// </summary>
[TestClass]
public class GatewayListenerHostnameHttpsAndOwnerCoverageBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route) =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void TcpListener_WithHostname_IsUnsupportedValue()
    {
        // Bug class: hostname on TCP must not be ignored and later treated as SNI.
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener
            {
                Name = "tcp",
                Port = 80,
                Protocol = "TCP",
                Hostname = "tcp.example",
            },
        ];
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "Accepted").Status);
        Assert.AreEqual("UnsupportedValue", Cond(listener.Conditions, "Accepted").Reason);
        StringAssert.Contains(Cond(listener.Conditions, "Accepted").Message ?? "", "must not set hostname");
    }

    [TestMethod]
    public void HttpsListener_PassthroughMode_IsUnsupportedValue()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener
            {
                Name = "https",
                Port = 443,
                Protocol = "HTTPS",
                Tls = new ListenerTls { Mode = "Passthrough" },
            },
        ];
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "Accepted").Status);
        Assert.AreEqual("UnsupportedValue", Cond(listener.Conditions, "Accepted").Reason);
        StringAssert.Contains(Cond(listener.Conditions, "Accepted").Message ?? "", "Terminate only");
    }

    [TestMethod]
    public void HttpsListener_MissingCertificateRefs_IsInvalidCertificateRef()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener
            {
                Name = "https",
                Port = 443,
                Protocol = "HTTPS",
                Tls = new ListenerTls { Mode = "Terminate", CertificateRefs = [] },
            },
        ];
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "ResolvedRefs").Status);
        Assert.AreEqual("InvalidCertificateRef", Cond(listener.Conditions, "ResolvedRefs").Reason);
        StringAssert.Contains(Cond(listener.Conditions, "ResolvedRefs").Message ?? "", "certificateRefs");
    }

    [TestMethod]
    public void HttpListener_AllowedRoutesKinds_PartialUnsupported_SetsInvalidRouteKinds()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners[0].AllowedRoutes = new AllowedRoutes
        {
            Kinds =
            [
                new RouteGroupKind { Group = GatewayApiNames.Group, Kind = GatewayApiNames.HttpRouteKind },
                new RouteGroupKind { Group = GatewayApiNames.Group, Kind = GatewayApiNames.TcpRouteKind },
            ],
        };
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "ResolvedRefs").Status);
        Assert.AreEqual("InvalidRouteKinds", Cond(listener.Conditions, "ResolvedRefs").Reason);
        StringAssert.Contains(Cond(listener.Conditions, "ResolvedRefs").Message ?? "", "also lists kinds");
    }

    [TestMethod]
    public void TcpRoute_SecondOwnerOnSameListener_IsRejected()
    {
        var b = Basic();
        b.AddService("default", "other", 8080, "10.0.0.9");
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        b.TcpRoutes.Add(TcpRoute("first", "default", Backend("web")));
        b.TcpRoutes.Add(TcpRoute("second", "default", Backend("other")));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("True", Cond(Parent(result, StatusKind.TcpRoute, "first").Conditions, "Accepted").Status);
        var second = Cond(Parent(result, StatusKind.TcpRoute, "second").Conditions, "Accepted");
        Assert.AreEqual("False", second.Status);
        Assert.AreEqual("NotAllowedByListeners", second.Reason);
        StringAssert.Contains(second.Message ?? "", "another TCPRoute");
    }

    [TestMethod]
    public void UdpRoute_SecondOwnerOnSameListener_IsRejected()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Udp("udp", 53)];
        b.AddUdpService("default", "dns", 53, "10.0.0.53");
        b.AddUdpService("default", "dns2", 53, "10.0.0.54");
        b.UdpRoutes.Add(UdpRoute("first", "default", Backend("dns", 53)));
        b.UdpRoutes.Add(UdpRoute("second", "default", Backend("dns2", 53)));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("True", Cond(Parent(result, StatusKind.UdpRoute, "first").Conditions, "Accepted").Status);
        var second = Cond(Parent(result, StatusKind.UdpRoute, "second").Conditions, "Accepted");
        Assert.AreEqual("False", second.Status);
        Assert.AreEqual("NotAllowedByListeners", second.Reason);
        StringAssert.Contains(second.Message ?? "", "another UDPRoute");
    }
}
