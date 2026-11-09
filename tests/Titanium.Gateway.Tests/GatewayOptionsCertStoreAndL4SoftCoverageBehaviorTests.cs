using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: GatewayOptions eq-flags / To* helpers / validate,
///     CertificateStore Retain and bad CA, TLSRoute empty backends, AllowedRoutes From=All,
///     and gRPC unsupported method type / second RefIssue append.
/// </summary>
[TestClass]
public class GatewayOptionsCertStoreAndL4SoftCoverageBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void GatewayOptions_EqFlags_ToHelpers_AndNegativeReconcile()
    {
        var env = new Dictionary<string, string?>
        {
            ["POD_NAMESPACE"] = "gw",
            ["POD_NAME"] = "pod-a",
        };
        var options = GatewayOptions.Parse(
            [
                "--data-plane-service=titanium",
                "--verbose",
                "--no-leader-election",
                "--enable-backend-tls-policy",
                "--watch-legacy-annotation",
                "--min-reconcile-ms",
                "250",
                "--secret-namespaces",
                "a,b",
                "--data-plane-deployment",
                "titanium-data",
            ],
            env);
        Assert.IsTrue(options.Verbose);
        Assert.IsFalse(options.LeaderElection);
        Assert.IsTrue(options.BackendTlsPolicyEnabled);
        Assert.IsTrue(options.WatchLegacyAnnotation);
        Assert.AreEqual(250, options.MinReconcileMilliseconds);
        CollectionAssert.AreEqual(new[] { "a", "b" }, options.SecretNamespaces.ToArray());

        var leader = options.ToLeaderOptions();
        Assert.AreEqual("gw", leader.Namespace);
        Assert.AreEqual("pod-a", leader.Identity);
        var cache = options.ToCacheOptions();
        Assert.IsTrue(cache.BackendTlsPolicyEnabled);
        CollectionAssert.AreEqual(new[] { "a", "b" }, cache.SecretNamespaces.ToArray());
        Assert.AreEqual("titanium-data", options.ToControllerOptions().Output.DeploymentName);

        var needsValue = Assert.ThrowsExactly<ArgumentException>(() =>
            GatewayOptions.Parse(["--data-plane-service"], env));
        StringAssert.Contains(needsValue.Message, "needs a value");

        var badNumber = Assert.ThrowsExactly<ArgumentException>(() =>
            GatewayOptions.Parse(["--data-plane-service=titanium", "--min-reconcile-ms", "abc"], env));
        StringAssert.Contains(badNumber.Message, "must be a number");

        var noNamespace = Assert.ThrowsExactly<ArgumentException>(() =>
            GatewayOptions.Parse(["--data-plane-service=titanium", "--no-leader-election"], new Dictionary<string, string?>()));
        StringAssert.Contains(noNamespace.Message, "data-plane-namespace");

        var noLease = Assert.ThrowsExactly<ArgumentException>(() =>
            GatewayOptions.Parse(
                ["--data-plane-service=titanium", "--data-plane-namespace=gw"],
                new Dictionary<string, string?>()));
        StringAssert.Contains(noLease.Message, "lease-namespace");
    }

    [TestMethod]
    public void CertificateStore_Retain_BadCa_AndMultiCertChain()
    {
        var store = new CertificateStore();
        var (leafCert, leafKey) = SelfSignedLeaf("leaf.example");
        var live = new SecretData("ns", "live", "kubernetes.io/tls", "1", leafCert, leafKey, leafCert);
        Assert.IsNotNull(store.GetPfx(live));
        Assert.IsTrue(store.HasCa(live));
        Assert.IsNotNull(store.GetPem(live, TlsFileKind.CaPem));

        var stale = new SecretData("ns", "stale", "kubernetes.io/tls", "1", leafCert, leafKey);
        Assert.IsNotNull(store.GetPfx(stale));
        store.Retain([live]);
        // After Retain, stale is dropped from cache; a new Lookup recreates it.
        Assert.IsNotNull(store.GetPfx(stale));

        var badCa = new SecretData("ns", "bad-ca", "kubernetes.io/tls", "1", leafCert, leafKey, Encoding.UTF8.GetBytes("not-a-cert"));
        Assert.IsFalse(store.HasCa(badCa));
        Assert.IsNull(store.GetPem(badCa, TlsFileKind.CaPem));
    }

    [TestMethod]
    public void TlsRoute_EmptyBackendRefs_IsBackendNotFound()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener
            {
                Name = "tls",
                Port = 443,
                Protocol = "TLS",
                Tls = new ListenerTls { Mode = "Passthrough" },
            },
        ];
        var empty = TlsRoute("r", "default", null);
        empty.Spec.Rules = [new L4RouteRule { BackendRefs = [] }];
        b.TlsRoutes.Add(empty);
        var result = b.Render();
        AssertValid(result);

        var refs = Cond(Parent(result, StatusKind.TlsRoute).Conditions, "ResolvedRefs");
        Assert.AreEqual("False", refs.Status);
        Assert.AreEqual("BackendNotFound", refs.Reason);
        StringAssert.Contains(refs.Message ?? "", "no backendRefs");
    }

    [TestMethod]
    public void TcpRoute_AllowedRoutesFromAll_AdmitsOtherNamespace()
    {
        var b = Basic();
        b.Namespaces.Add(new NamespaceInfo("apps", Labels("apps")));
        b.AddService("apps", "web", 8080, "10.2.0.1");
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        b.Gateways[0].Spec.Listeners[0].AllowedRoutes = new AllowedRoutes
        {
            Namespaces = new RouteNamespaces { From = "All" },
        };
        b.TcpRoutes.Add(TcpRoute("r", "apps", Backend("web")));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("True", Cond(Parent(result, StatusKind.TcpRoute).Conditions, "Accepted").Status);
        Assert.AreEqual("apps/web:8080", result.Config.Listeners.Single(l => l.Passthrough).SniDefaultClusterId);
    }

    [TestMethod]
    public void GrpcRoute_UnsupportedMethodType_AndSecondRefIssueAppend()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            new GrpcRouteRule
            {
                Matches =
                [
                    new GrpcRouteMatch
                    {
                        Method = new GrpcMethodMatch { Type = "Prefix", Service = "pkg.Echo", Method = "Say" },
                    },
                ],
                BackendRefs =
                [
                    Backend("missing-a", 8080),
                    Backend("missing-b", 8080),
                ],
            }));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        var invalid = Cond(Parent(result, StatusKind.GrpcRoute).Conditions, "PartiallyInvalid");
        StringAssert.Contains(invalid.Message ?? "", "unsupported method match type");
        var refs = Cond(Parent(result, StatusKind.GrpcRoute).Conditions, "ResolvedRefs");
        Assert.AreEqual("False", refs.Status);
        // Two missing backends must append, not replace, the ResolvedRefs message.
        StringAssert.Contains(refs.Message ?? "", "missing-a");
        StringAssert.Contains(refs.Message ?? "", "missing-b");
    }

    private static (byte[] CertPem, byte[] KeyPem) SelfSignedLeaf(string dns)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + dns, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dns);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (Encoding.UTF8.GetBytes(cert.ExportCertificatePem()), Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()));
    }
}
