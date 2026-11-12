using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Fat L4 leftovers: listener hostname inheritance gates, cross-ns grant miss, invalid backend kind/port.
/// </summary>
[TestClass]
public class GatewayTranslatorL4HostnameBackendBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void TlsRoute_WithoutHostnames_OnListenerWithHostname_IsNotAllowed()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [TlsPassthrough("tls", 443, "sni.example")];
        b.TlsRoutes.Add(TlsRoute("r", "default", null, Backend("web")));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("False", Cond(Parent(result, StatusKind.TlsRoute).Conditions, "Accepted").Status);
        Assert.AreEqual("NotAllowedByListeners", Cond(Parent(result, StatusKind.TlsRoute).Conditions, "Accepted").Reason);
        Assert.AreEqual(0, result.Config.Listeners.Count);
    }

    [TestMethod]
    public void TlsRoute_HostnameMismatch_IsNotAllowed()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [TlsPassthrough("tls", 443, "sni.example")];
        b.TlsRoutes.Add(TlsRoute("r", "default", ["other.example"], Backend("web")));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("False", Cond(Parent(result, StatusKind.TlsRoute).Conditions, "Accepted").Status);
        Assert.AreEqual("NotAllowedByListeners", Cond(Parent(result, StatusKind.TlsRoute).Conditions, "Accepted").Reason);
    }

    [TestMethod]
    public void TlsRoute_IntersectingHostname_AttachesSniRoute()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [TlsPassthrough("tls", 443, "*.example")];
        b.TlsRoutes.Add(TlsRoute("r", "default", ["app.example"], Backend("web")));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("True", Cond(Parent(result, StatusKind.TlsRoute).Conditions, "Accepted").Status);
        var listener = result.Config.Listeners.Single(l => l.Passthrough && l.Port == 8443);
        Assert.AreEqual(1, listener.SniRoutes.Count);
        Assert.AreEqual("app.example", listener.SniRoutes[0].ServerName);
    }

    [TestMethod]
    public void TcpRoute_InvalidBackendKind_IsInvalidKind()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        var route = TcpRoute("r", "default", new HttpBackendRef
        {
            Name = "web",
            Port = 8080,
            Kind = "ServiceImport",
            Group = "net.example",
        });
        b.TcpRoutes.Add(route);
        var result = b.Render();
        AssertValid(result);

        var refs = Cond(Parent(result, StatusKind.TcpRoute).Conditions, "ResolvedRefs");
        Assert.AreEqual("False", refs.Status);
        Assert.AreEqual("InvalidKind", refs.Reason);
    }

    [TestMethod]
    public void TcpRoute_MissingPort_IsBackendNotFound()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        b.TcpRoutes.Add(TcpRoute("r", "default", new HttpBackendRef { Name = "web" }));
        var result = b.Render();
        AssertValid(result);

        var refs = Cond(Parent(result, StatusKind.TcpRoute).Conditions, "ResolvedRefs");
        Assert.AreEqual("False", refs.Status);
        Assert.AreEqual("BackendNotFound", refs.Reason);
        StringAssert.Contains(refs.Message!, "must name a port");
    }

    [TestMethod]
    public void TcpRoute_CrossNamespaceWithoutGrant_IsRefNotPermitted()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        b.Namespaces.Add(new NamespaceInfo("other", Labels("other")));
        b.AddService("other", "web", 8080, "10.0.0.9");
        b.TcpRoutes.Add(TcpRoute("r", "default", new HttpBackendRef
        {
            Name = "web",
            Namespace = "other",
            Port = 8080,
        }));
        var result = b.Render();
        AssertValid(result);

        var refs = Cond(Parent(result, StatusKind.TcpRoute).Conditions, "ResolvedRefs");
        Assert.AreEqual("False", refs.Status);
        Assert.AreEqual("RefNotPermitted", refs.Reason);
    }
}
