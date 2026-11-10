using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Remaining L4/listener leftovers still soft on tip coverage: TLS mode that is neither
///     Passthrough nor Terminate, UDP with zero ready endpoints, and TCP backend port miss.
/// </summary>
[TestClass]
public class GatewayL4EmptyEndpointsAndTlsModeCoverageBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void TlsListener_ModeNeitherPassthroughNorTerminate_IsUnsupportedValue()
    {
        // Bug class: a typo mode must not be treated as Terminate or accepted as Passthrough.
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener
            {
                Name = "tls",
                Port = 443,
                Protocol = "TLS",
                Tls = new ListenerTls { Mode = "Mutual" },
            },
        ];
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "Accepted").Status);
        Assert.AreEqual("UnsupportedProtocol", Cond(listener.Conditions, "Accepted").Reason);
        StringAssert.Contains(Cond(listener.Conditions, "Accepted").Message ?? "", "Passthrough or Terminate");
        Assert.AreEqual(0, result.Config.Listeners.Count);
    }

    [TestMethod]
    public void UdpRoute_ServiceWithNoReadyEndpoints_LeavesListenerWithoutCluster()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Udp("udp", 53)];
        b.AddUdpService("default", "dns", 53);
        b.UdpRoutes.Add(UdpRoute("r", "default", Backend("dns", 53)));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("True", Cond(Parent(result, StatusKind.UdpRoute).Conditions, "Accepted").Status);
        Assert.AreEqual(0, result.Config.Listeners.Count(l => string.Equals(l.Type, "udp", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void TcpRoute_BackendPortMissingOnService_IsBackendNotFound()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        b.AddService("default", "web", 8080, "10.0.0.8");
        b.TcpRoutes.Add(TcpRoute("r", "default", Backend("web", 9090)));
        var result = b.Render();
        AssertValid(result);

        var refs = Cond(Parent(result, StatusKind.TcpRoute).Conditions, "ResolvedRefs");
        Assert.AreEqual("False", refs.Status);
        Assert.AreEqual("BackendNotFound", refs.Reason);
        StringAssert.Contains(refs.Message ?? "", "has no port 9090");
        Assert.AreEqual(0, result.Config.Listeners.Count);
    }
}
