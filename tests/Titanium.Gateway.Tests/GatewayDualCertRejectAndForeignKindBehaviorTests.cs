using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Remaining Listener/Helpers/CertificateStore leftovers on develop Sonar: dual certificate
///     Reject message append, foreign AllowedRoutes group, GetPem null arms, and nested wildcards.
/// </summary>
[TestClass]
public class GatewayDualCertRejectAndForeignKindBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void Https_TwoMissingSecrets_AppendsBothRejectMessages()
    {
        // Bug class: a second certificateRefs failure must append, not replace, the ResolvedRefs message.
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener
            {
                Name = "https",
                Port = 443,
                Protocol = "HTTPS",
                Tls = new ListenerTls
                {
                    Mode = "Terminate",
                    CertificateRefs =
                    [
                        new SecretObjectReference { Name = "missing-a" },
                        new SecretObjectReference { Name = "missing-b" },
                    ],
                },
            },
        ];
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "ResolvedRefs").Status);
        var message = Cond(listener.Conditions, "ResolvedRefs").Message ?? "";
        StringAssert.Contains(message, "missing-a");
        StringAssert.Contains(message, "missing-b");
        Assert.AreEqual(0, result.Config.Listeners.Count);
    }

    [TestMethod]
    public void UdpListener_AllowedRoutesEmptyKinds_InvalidatesListener()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener
            {
                Name = "udp",
                Port = 53,
                Protocol = "UDP",
                AllowedRoutes = new AllowedRoutes
                {
                    Kinds = [new RouteGroupKind { Group = "example.com", Kind = "HTTPRoute" }],
                },
            },
        ];
        b.AddUdpService("default", "dns", 53, "10.0.0.9");
        b.UdpRoutes.Add(UdpRoute("r", "default", Backend("dns", 53)));
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "ResolvedRefs").Status);
        Assert.AreEqual("InvalidRouteKinds", Cond(listener.Conditions, "ResolvedRefs").Reason);
        StringAssert.Contains(Cond(listener.Conditions, "ResolvedRefs").Message ?? "", "no kind");
    }

    [TestMethod]
    public void CertificateStore_GetPem_NullWhenConvertFails_AndCaWhenPresent()
    {
        var store = new CertificateStore();
        var empty = new SecretData("ns", "empty", "kubernetes.io/tls", "1", null, null);
        Assert.IsNull(store.GetPfx(empty));
        Assert.IsNull(store.GetPem(empty, TlsFileKind.CertificatePem));
        Assert.IsNull(store.GetPem(empty, TlsFileKind.PrivateKeyPem));
        Assert.IsNull(store.GetPem(empty, TlsFileKind.CaPem));

        var (cert, key) = SelfSigned("pem.example");
        var withCa = new SecretData("ns", "with-ca", "kubernetes.io/tls", "1", cert, key, cert);
        Assert.IsNotNull(store.GetPfx(withCa));
        Assert.IsNotNull(store.GetPem(withCa, TlsFileKind.CertificatePem));
        Assert.IsNotNull(store.GetPem(withCa, TlsFileKind.PrivateKeyPem));
        Assert.IsNotNull(store.GetPem(withCa, TlsFileKind.CaPem));
    }

    [TestMethod]
    public void Hostnames_NestedWildcards_PreferMoreSpecific()
    {
        Assert.IsTrue(Hostnames.TryIntersect("*.example.com", "*.api.example.com", out var nested));
        Assert.AreEqual("*.api.example.com", nested);
        Assert.IsTrue(Hostnames.TryIntersect("*.api.example.com", "*.example.com", out var again));
        Assert.AreEqual("*.api.example.com", again);
    }

    private static (byte[] CertPem, byte[] KeyPem) SelfSigned(string dns)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=" + dns,
            key,
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (
            System.Text.Encoding.UTF8.GetBytes(cert.ExportCertificatePem()),
            System.Text.Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()));
    }
}
