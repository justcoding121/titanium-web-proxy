using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Gateway.Tests;

[TestClass]
public class GatewayTranslatorListenerTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static StatusPatch GatewayPatch(RenderResult result, string name = "gw") =>
        result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway && p.Name == name);

    [TestMethod]
    public void BasicGateway_RendersAMappedTransparentListenerAndAdmin()
    {
        var b = StateBuilder.Basic();
        var result = b.Render();

        var listener = result.Config.Listeners.Single();
        Assert.AreEqual(8080, listener.Port);
        Assert.AreEqual("transparent", listener.Type);
        Assert.IsFalse(listener.DecryptSsl);
        Assert.AreEqual(9901, result.Config.Server?.Admin?.Port);
        Assert.AreEqual("TITANIUM_ADMIN_TOKEN", result.Config.Server?.Admin?.AuthTokenEnv);
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count);
    }

    [TestMethod]
    public void GatewayClass_IsAcceptedOnlyForOurController()
    {
        var b = StateBuilder.Basic();
        b.Classes.Add(StateBuilder.Class("other", "example.com/other"));
        var result = b.Render();

        var patches = result.StatusPatches.Where(p => p.Kind == StatusKind.GatewayClass).ToList();
        Assert.AreEqual(1, patches.Count);
        Assert.AreEqual("titanium", patches[0].Name);
        Assert.AreEqual("True", Cond(patches[0].Conditions, "Accepted").Status);
    }

    [TestMethod]
    public void Gateway_OfAForeignClass_IsIgnored()
    {
        var b = StateBuilder.Basic();
        b.Classes.Add(StateBuilder.Class("other", "example.com/other"));
        b.Gateways.Add(StateBuilder.Gateway("foreign", "default", [StateBuilder.Http("http", 80)], "other"));
        var result = b.Render();

        Assert.IsFalse(result.StatusPatches.Any(p => p.Name == "foreign"));
    }

    [TestMethod]
    public void UnmappedPort_IsNotProgrammedWithPortUnavailable()
    {
        var b = StateBuilder.Basic();
        b.Gateways[0].Spec.Listeners.Add(StateBuilder.Http("odd", 8081));
        var result = b.Render();

        var listener = GatewayPatch(result).Listeners!.Single(l => l.Name == "odd");
        Assert.AreEqual("PortUnavailable", Cond(listener.Conditions, "Accepted").Reason);
        Assert.AreEqual("False", Cond(listener.Conditions, "Programmed").Status);
        Assert.AreEqual(1, result.Config.Listeners.Count);
        var gateway = GatewayPatch(result);
        Assert.AreEqual("True", Cond(gateway.Conditions, "Accepted").Status);
        Assert.AreEqual("ListenersNotValid", Cond(gateway.Conditions, "Accepted").Reason);
    }

    [TestMethod]
    public void UnsupportedProtocol_IsRejected()
    {
        var b = StateBuilder.Basic();
        b.Gateways[0].Spec.Listeners = [new GatewayListener { Name = "tcp", Port = 80, Protocol = "TCP" }];
        var result = b.Render();

        var gateway = GatewayPatch(result);
        Assert.AreEqual("UnsupportedProtocol", Cond(gateway.Listeners![0].Conditions, "Accepted").Reason);
        Assert.AreEqual("False", Cond(gateway.Conditions, "Accepted").Status);
        Assert.AreEqual(0, result.Config.Listeners.Count);
    }

    [TestMethod]
    public void SpecAddresses_AreUnsupported()
    {
        var b = StateBuilder.Basic();
        b.Gateways[0].Spec.Addresses = [new GatewaySpecAddress { Type = "IPAddress", Value = "1.2.3.4" }];
        var result = b.Render();

        Assert.AreEqual("UnsupportedAddress", Cond(GatewayPatch(result).Conditions, "Accepted").Reason);
    }

    [TestMethod]
    public void Https_WithATlsSecret_DecryptsAndBindsTheCertificate()
    {
        var b = StateBuilder.Basic();
        b.Gateways[0].Spec.Listeners = [StateBuilder.Https("https", 443, "app.example.com", "cert")];
        b.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));
        var result = b.Render();

        var listener = result.Config.Listeners.Single();
        Assert.AreEqual(8443, listener.Port);
        Assert.IsTrue(listener.DecryptSsl);
        var item = result.Config.Certificates!.Items.Single();
        Assert.AreEqual("/etc/titanium-tls/default.cert.pfx", item.Path);
        CollectionAssert.AreEqual(new[] { "app.example.com" }, item.Hosts.ToArray());
        var file = result.TlsFiles.Single();
        Assert.AreEqual("default.cert.pfx", file.FileName);
        Assert.AreEqual("cert", file.SecretName);
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count);
    }

    [TestMethod]
    public void Https_WithoutAHostname_BindsTheCertificateForEveryName()
    {
        var b = StateBuilder.Basic();
        b.Gateways[0].Spec.Listeners = [StateBuilder.Https("https", 443, null, "cert")];
        b.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));

        CollectionAssert.AreEqual(new[] { "*" }, b.Render().Config.Certificates!.Items.Single().Hosts.ToArray());
    }

    [TestMethod]
    [DataRow("missing", "InvalidCertificateRef")]
    [DataRow("opaque", "InvalidCertificateRef")]
    [DataRow("other/cert", "RefNotPermitted")]
    public void Https_BadCertificateRef_IsNotProgrammed(string secret, string reason)
    {
        var b = StateBuilder.Basic();
        var listener = StateBuilder.Https("https", 443, null, "x");
        if (secret.Contains('/', StringComparison.Ordinal))
        {
            var parts = secret.Split('/');
            listener.Tls!.CertificateRefs = [new SecretObjectReference { Name = parts[1], Namespace = parts[0] }];
        }
        else
        {
            listener.Tls!.CertificateRefs = [new SecretObjectReference { Name = secret }];
        }

        b.Gateways[0].Spec.Listeners = [listener];
        b.Secrets.Add(new SecretInfo("default", "opaque", "Opaque", false, false));
        b.Secrets.Add(new SecretInfo("other", "cert", "kubernetes.io/tls", true, true));
        var result = b.Render();

        var status = GatewayPatch(result).Listeners!.Single();
        Assert.AreEqual("False", Cond(status.Conditions, "ResolvedRefs").Status);
        Assert.AreEqual(reason, Cond(status.Conditions, "ResolvedRefs").Reason);
        Assert.AreEqual("False", Cond(status.Conditions, "Programmed").Status);
        Assert.AreEqual(0, result.Config.Listeners.Count);
        Assert.AreEqual(0, result.TlsFiles.Count);
    }

    [TestMethod]
    public void Https_CrossNamespaceCertificate_NeedsAReferenceGrant()
    {
        var b = StateBuilder.Basic();
        var listener = StateBuilder.Https("https", 443, null, "x");
        listener.Tls!.CertificateRefs = [new SecretObjectReference { Name = "cert", Namespace = "certs" }];
        b.Gateways[0].Spec.Listeners = [listener];
        b.Secrets.Add(new SecretInfo("certs", "cert", "kubernetes.io/tls", true, true));
        b.Grants.Add(new ReferenceGrant
        {
            Metadata = new ObjectMeta { Name = "g", Namespace = "certs" },
            Spec = new ReferenceGrantSpec
            {
                From = [new ReferenceGrantFrom { Group = GatewayApiNames.Group, Kind = "Gateway", Namespace = "default" }],
                To = [new ReferenceGrantTo { Group = string.Empty, Kind = "Secret" }],
            },
        });
        var result = b.Render();

        Assert.AreEqual("certs.cert.pfx", result.TlsFiles.Single().FileName);
    }

    [TestMethod]
    public void Https_PassthroughMode_IsRejected()
    {
        var b = StateBuilder.Basic();
        var listener = StateBuilder.Https("https", 443, null, "cert");
        listener.Tls!.Mode = "Passthrough";
        b.Gateways[0].Spec.Listeners = [listener];
        b.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));

        Assert.AreEqual(0, b.Render().Config.Listeners.Count);
    }

    [TestMethod]
    public void SamePortDifferentProtocol_ConflictsAndTheOlderGatewayWins()
    {
        var b = StateBuilder.Basic();
        b.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));
        b.Gateways.Add(StateBuilder.Gateway("newer", "default", [StateBuilder.Https("https", 80, null, "cert")], created: StateBuilder.T0.AddDays(1)));
        var result = b.Render();

        var newer = GatewayPatch(result, "newer").Listeners!.Single();
        Assert.AreEqual("True", Cond(newer.Conditions, "Conflicted").Status);
        Assert.AreEqual("ProtocolConflict", Cond(newer.Conditions, "Conflicted").Reason);
        Assert.AreEqual("NoConflicts", Cond(GatewayPatch(result).Listeners!.Single().Conditions, "Conflicted").Reason);
        Assert.IsFalse(result.Config.Listeners.Single().DecryptSsl);
    }

    [TestMethod]
    public void SamePortSameHostname_IsAHostnameConflict()
    {
        var b = StateBuilder.Basic();
        b.Gateways.Add(StateBuilder.Gateway("newer", "default", [StateBuilder.Http("http", 80)], created: StateBuilder.T0.AddDays(1)));
        var result = b.Render();

        Assert.AreEqual(
            "HostnameConflict",
            Cond(GatewayPatch(result, "newer").Listeners!.Single().Conditions, "Conflicted").Reason);
    }

    [TestMethod]
    public void SamePortDistinctHostnames_ShareOneDataPlaneListener()
    {
        var b = StateBuilder.Basic();
        b.Gateways[0].Spec.Listeners = [StateBuilder.Http("a", 80, "a.example.com"), StateBuilder.Http("b", 80, "b.example.com")];
        var result = b.Render();

        Assert.AreEqual(1, result.Config.Listeners.Count);
    }

    [TestMethod]
    public void Programmed_TurnsTrueOnlyWhenTheDataPlaneRunsTheHash()
    {
        var b = StateBuilder.Basic();
        var result = b.Render();

        var pending = Cond(GatewayPatch(result).Conditions, "Programmed");
        Assert.AreEqual("False", pending.Status);
        Assert.AreEqual("Pending", pending.Reason);

        var stale = result.Finalize(new DataPlaneObservation(["old", "abc"]), "abc");
        Assert.AreEqual("False", Cond(stale.Single(p => p.Kind == StatusKind.Gateway).Conditions, "Programmed").Status);

        var live = result.Finalize(new DataPlaneObservation(["abc", "abc"]), "abc");
        var gateway = live.Single(p => p.Kind == StatusKind.Gateway);
        Assert.AreEqual("True", Cond(gateway.Conditions, "Programmed").Status);
        Assert.AreEqual("True", Cond(gateway.Listeners![0].Conditions, "Programmed").Status);
        Assert.AreEqual("203.0.113.10", gateway.Addresses!.Single().Value);
    }

    [TestMethod]
    public void NoDataPlaneAddress_ReportsAddressNotAssigned()
    {
        var b = StateBuilder.Basic();
        b.Services.RemoveAll(s => s.Name == "titanium-data");
        var result = b.Render();

        Assert.AreEqual("AddressNotAssigned", Cond(GatewayPatch(result).Conditions, "Programmed").Reason);
        Assert.AreEqual(0, GatewayPatch(result).Addresses!.Count);
    }

    [TestMethod]
    public void ClusterIpService_IsTheAddressFallback()
    {
        var b = StateBuilder.Basic();
        b.Services.RemoveAll(s => s.Name == "titanium-data");
        b.Services.Add(new ServiceInfo("titanium-system", "titanium-data", "ClusterIP", "10.96.0.9", [], []));

        Assert.AreEqual("10.96.0.9", GatewayPatch(b.Render()).Addresses!.Single().Value);
    }

    [TestMethod]
    public void Render_IsDeterministic()
    {
        var b = StateBuilder.Basic();
        b.Routes.Add(StateBuilder.Route("r", "default", ["a.example.com"], StateBuilder.Rule("web", 8080, StateBuilder.PathPrefix("/x"))));

        Assert.AreEqual(TwpConfigLoader.ToJson(b.Render().Config), TwpConfigLoader.ToJson(b.Render().Config));
    }
}
