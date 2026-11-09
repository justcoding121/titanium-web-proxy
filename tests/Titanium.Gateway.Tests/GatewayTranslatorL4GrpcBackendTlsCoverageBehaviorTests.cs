using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Fat L4/gRPC/BackendTLS leftovers: HTTPS terminate + TCPRoute, AllowedRoutes selector on TCP,
///     gRPC response/mirror filters, multi-CA PEM concat, and weight-zero backend skip.
/// </summary>
[TestClass]
public class GatewayTranslatorL4GrpcBackendTlsCoverageBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    private static string SelfSignedPem(string cn)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=" + cn,
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return cert.ExportCertificatePem();
    }

    private static GatewayListener TlsTerminate(string name, int port, params string[] secrets) => new()
    {
        Name = name,
        Port = port,
        Protocol = "TLS",
        Tls = new ListenerTls
        {
            Mode = "Terminate",
            CertificateRefs = secrets.Select(s => new SecretObjectReference { Name = s }).ToList(),
        },
    };

    [TestMethod]
    public void TcpRoute_OnTlsTerminateListener_OwnsTerminateTlsListener_AndEmitsCert()
    {
        // Bug class: TCPRoute on TLS terminate never sets TerminateTls, or serving certs are omitted
        // so the data plane rejects terminateTls for missing certificates.items.
        var b = Basic();
        b.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));
        b.Gateways[0].Spec.Listeners = [TlsTerminate("tls", 443, "cert")];
        b.TcpRoutes.Add(TcpRoute("r", "default", Backend("web")));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("True", Cond(Parent(result, StatusKind.TcpRoute).Conditions, "Accepted").Status);
        var listener = result.Config.Listeners.Single(l => l.Port == 8443);
        Assert.IsTrue(listener.TerminateTls);
        Assert.IsTrue(listener.Passthrough);
        Assert.AreEqual("default/web:8080", listener.SniDefaultClusterId);
        Assert.IsFalse(listener.DecryptSsl);
        Assert.IsNotNull(result.Config.Certificates?.Items);
        Assert.IsTrue(result.Config.Certificates!.Items.Any(i => i.Path.Contains("default.cert.pfx", StringComparison.Ordinal)));
        Assert.AreEqual(1, result.TlsFiles.Count(f => f.SecretName == "cert"));
    }

    [TestMethod]
    public void TcpRoute_AllowedRoutesSelector_AdmitsMatchingNamespace()
    {
        var b = Basic();
        b.Namespaces.Add(new NamespaceInfo("apps", new Dictionary<string, string> { ["team"] = "blue" }));
        b.AddService("apps", "web", 8080, "10.1.0.1");
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        b.Gateways[0].Spec.Listeners[0].AllowedRoutes = new AllowedRoutes
        {
            Namespaces = new RouteNamespaces
            {
                From = "Selector",
                Selector = new LabelSelector { MatchLabels = new Dictionary<string, string> { ["team"] = "blue" } },
            },
        };
        b.TcpRoutes.Add(TcpRoute("r", "apps", Backend("web")));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual("True", Cond(Parent(result, StatusKind.TcpRoute).Conditions, "Accepted").Status);
        var listener = result.Config.Listeners.Single(l => l.Port == 8080 && l.Passthrough);
        Assert.AreEqual("apps/web:8080", listener.SniDefaultClusterId);

        b.Namespaces[^1] = new NamespaceInfo("apps", new Dictionary<string, string> { ["team"] = "red" });
        b.TcpRoutes.Clear();
        b.TcpRoutes.Add(TcpRoute("r", "apps", Backend("web")));
        var denied = b.Render();
        AssertValid(denied);
        Assert.AreEqual("False", Cond(Parent(denied, StatusKind.TcpRoute).Conditions, "Accepted").Status);
        Assert.AreEqual("NotAllowedByListeners", Cond(Parent(denied, StatusKind.TcpRoute).Conditions, "Accepted").Reason);
    }

    [TestMethod]
    public void GrpcRoute_ResponseHeaderModifier_AndRequestMirror_Apply()
    {
        var b = Basic();
        b.AddService("default", "shadow", 8080, "10.7.0.1");
        b.GrpcRoutes.Add(new GrpcRoute
        {
            Metadata = new ObjectMeta { Name = "r", Namespace = "default", Generation = 1, CreationTimestamp = T0 },
            Spec = new GrpcRouteSpec
            {
                ParentRefs = [ParentGw()],
                Rules =
                [
                    new GrpcRouteRule
                    {
                        Filters =
                        [
                            new GrpcRouteFilter
                            {
                                Type = "ResponseHeaderModifier",
                                ResponseHeaderModifier = new HttpHeaderFilter
                                {
                                    Set = [new HttpHeader { Name = "x-svc", Value = "echo" }],
                                    Remove = ["server"],
                                },
                            },
                            new GrpcRouteFilter
                            {
                                Type = "RequestMirror",
                                RequestMirror = new HttpRequestMirror
                                {
                                    BackendRef = new BackendObjectReference { Name = "shadow", Port = 8080 },
                                    Fraction = new MirrorFraction { Numerator = 1, Denominator = 2 },
                                },
                            },
                        ],
                        BackendRefs = [Backend("web")],
                        Matches = [new GrpcRouteMatch { Method = new GrpcMethodMatch { Service = "pkg.Echo", Method = "Say" } }],
                    },
                ],
            },
        });
        var result = b.Render();
        AssertValid(result);

        var route = result.Config.UserRoutes().Single();
        Assert.IsTrue(route.Transforms!.Any(t =>
            t.Kind == TransformKinds.ResponseHeaderSet &&
            t.Parameters is not null &&
            t.Parameters.TryGetValue("name", out var name) &&
            string.Equals(name, "x-svc", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(route.Transforms!.Any(t => t.Kind == TransformKinds.ResponseHeaderRemove));
        Assert.IsNotNull(route.Mirror);
        Assert.AreEqual("default/shadow:8080", route.Mirror!.ClusterId);
        Assert.AreEqual(50, route.Mirror.SamplePercent);
    }

    [TestMethod]
    public void GrpcRoute_ZeroWeightBackend_IsSkipped_AndMissingBackendAppendsRefIssue()
    {
        var b = Basic();
        b.GrpcRoutes.Add(new GrpcRoute
        {
            Metadata = new ObjectMeta { Name = "r", Namespace = "default", Generation = 1, CreationTimestamp = T0 },
            Spec = new GrpcRouteSpec
            {
                ParentRefs = [ParentGw()],
                Rules =
                [
                    new GrpcRouteRule
                    {
                        BackendRefs =
                        [
                            new HttpBackendRef { Name = "web", Port = 8080, Weight = 0 },
                            new HttpBackendRef { Name = "ghost", Port = 8080, Weight = 1 },
                        ],
                        Matches = [new GrpcRouteMatch { Method = new GrpcMethodMatch { Service = "pkg.Echo", Method = "Say" } }],
                    },
                ],
            },
        });
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        var refs = Cond(Parent(result, StatusKind.GrpcRoute).Conditions, "ResolvedRefs");
        Assert.AreEqual("False", refs.Status);
        Assert.AreEqual("BackendNotFound", refs.Reason);
    }

    [TestMethod]
    public void GrpcRoute_WrongParentGroupOrKind_IsIgnored()
    {
        var b = Basic();
        var route = GrpcRoute("r", "default", null, GrpcRule("web"));
        route.Spec.ParentRefs =
        [
            new ParentReference { Name = "gw", Group = "example.com", Kind = GatewayApiNames.GatewayKind },
            new ParentReference { Name = "gw", Group = GatewayApiNames.Group, Kind = "Service" },
        ];
        b.GrpcRoutes.Add(route);
        var result = b.Render();
        AssertValid(result);

        Assert.IsFalse(result.StatusPatches.Any(p => p.Kind == StatusKind.GrpcRoute));
        Assert.AreEqual(0, result.Config.UserRoutes().Count());
    }

    [TestMethod]
    public void BackendTls_MultiConfigMapCa_ConcatenatesInlinePem()
    {
        var mount = Path.Combine(Path.GetTempPath(), "twp-backend-tls-multi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mount);
        try
        {
            var pemA = SelfSignedPem("ca-a");
            var pemB = SelfSignedPem("ca-b");
            var b = Basic();
            b.ConfigMaps.Add(new ConfigMapInfo("default", "ca-a", new Dictionary<string, string> { ["ca.crt"] = pemA }));
            b.ConfigMaps.Add(new ConfigMapInfo("default", "ca-b", new Dictionary<string, string> { ["ca.crt"] = pemB }));
            b.BackendTlsPolicies.Add(BackendTls(
                "pol",
                "default",
                "web",
                "web.svc.cluster.local",
                caConfigMaps: ["ca-a", "ca-b"]));
            b.Routes.Add(Route("r", "default", null, Rule("web")));
            var options = BackendTlsOptions with { TlsMountPath = mount };
            var result = b.Render(options);
            Assert.AreEqual(1, result.InlineTlsFiles.Count);
            foreach (var pair in result.InlineTlsFiles)
            {
                File.WriteAllBytes(Path.Combine(mount, pair.Key), pair.Value);
            }

            Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));
            var cluster = result.Config.Clusters.Single(c => c.Id == "default/web:8080");
            Assert.IsNotNull(cluster.HttpClient!.CaBundlePath);
            var fileName = Path.GetFileName(cluster.HttpClient.CaBundlePath);
            Assert.IsTrue(result.InlineTlsFiles.ContainsKey(fileName));
            var combined = Encoding.UTF8.GetString(result.InlineTlsFiles[fileName]);
            StringAssert.Contains(combined, "BEGIN CERTIFICATE");
            Assert.IsTrue(combined.Contains(pemA.Trim(), StringComparison.Ordinal));
            Assert.IsTrue(combined.Contains(pemB.Trim(), StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(mount, recursive: true);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    [TestMethod]
    public void BackendTls_DuplicateServiceKey_SecondPolicyIsSkipped()
    {
        var b = Basic();
        b.BackendTlsPolicies.Add(BackendTls(
            "first",
            "default",
            "web",
            "first.example",
            wellKnownCa: "System",
            created: T0));
        b.BackendTlsPolicies.Add(BackendTls(
            "second",
            "default",
            "web",
            "second.example",
            wellKnownCa: "System",
            created: T0));
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render(BackendTlsOptions);
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count);

        Assert.AreEqual(
            "first.example",
            result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient!.ServerName);
    }
}
