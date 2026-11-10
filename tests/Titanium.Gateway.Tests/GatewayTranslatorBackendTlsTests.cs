using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Behavior for BackendTLSPolicy translation onto cluster HttpClient TLS settings.
///     Bug classes: skip when feature flag is off, SAN policies dropped, System CA vs ConfigMap CA,
///     first-wins policy indexing, and incomplete validation refs.
/// </summary>
[TestClass]
public class GatewayTranslatorBackendTlsTests
{
    private static RenderResult RenderWithPolicy(StateBuilder b, HttpRoute route, GatewayTranslatorOptions? options = null)
    {
        b.Routes.Add(route);
        var result = b.Render(options ?? BackendTlsOptions);
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));
        return result;
    }

    private static HttpRoute WebRoute() =>
        Route("r", "default", null, Rule("web", 8080));

    [TestMethod]
    public void FeatureFlagOff_LeavesClustersCleartext()
    {
        // Bug class: BackendTLSPolicy objects leak into clusters when the chart flag is off.
        var b = Basic();
        b.BackendTlsPolicies.Add(BackendTls("pol", "default", "web", "web.svc.cluster.local", wellKnownCa: "System"));
        var result = RenderWithPolicy(b, WebRoute(), Options);

        var cluster = result.Config.Clusters.Single(c => c.Id == "default/web:8080");
        Assert.IsTrue(cluster.Destinations.All(d => !d.UseHttps));
        Assert.IsNull(cluster.HttpClient);
        Assert.AreEqual(0, result.InlineTlsFiles.Count);
    }

    [TestMethod]
    public void SystemCa_SetsHttpsAndServerName_WithoutInlinePem()
    {
        var b = Basic();
        b.BackendTlsPolicies.Add(BackendTls("pol", "default", "web", "web.svc.cluster.local", wellKnownCa: "System"));
        var result = RenderWithPolicy(b, WebRoute());

        var cluster = result.Config.Clusters.Single(c => c.Id == "default/web:8080");
        Assert.IsTrue(cluster.Destinations.All(d => d.UseHttps));
        Assert.AreEqual("web.svc.cluster.local", cluster.HttpClient!.ServerName);
        Assert.IsTrue(cluster.HttpClient.VerifyHostname);
        Assert.IsNull(cluster.HttpClient.CaBundlePath);
        Assert.AreEqual(0, result.InlineTlsFiles.Count);
    }

    [TestMethod]
    public void ConfigMapCa_EmitsInlinePem_AndPointsCaBundlePathAtMount()
    {
        // The controller publishes InlineTlsFiles into the data-plane mount after render; TwpConfigValidator
        // only sees the path once those bytes land on disk (same as production).
        var mount = Path.Combine(Path.GetTempPath(), "twp-backend-tls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mount);
        try
        {
            var b = Basic();
            // Minimal self-signed PEM so ValidateCaBundle accepts the materialised file.
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=backend-ca",
                rsa,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            var pem = cert.ExportCertificatePem();
            b.ConfigMaps.Add(new ConfigMapInfo("default", "backend-ca", new Dictionary<string, string>
            {
                ["ca.crt"] = pem,
            }));
            b.BackendTlsPolicies.Add(BackendTls(
                "pol",
                "default",
                "web",
                "web.svc.cluster.local",
                caConfigMaps: ["backend-ca"]));
            var options = BackendTlsOptions with { TlsMountPath = mount };
            b.Routes.Add(WebRoute());
            var result = b.Render(options);
            Assert.AreEqual(1, result.InlineTlsFiles.Count);
            foreach (var pair in result.InlineTlsFiles)
            {
                File.WriteAllBytes(Path.Combine(mount, pair.Key), pair.Value);
            }

            Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

            var cluster = result.Config.Clusters.Single(c => c.Id == "default/web:8080");
            Assert.IsTrue(cluster.Destinations.All(d => d.UseHttps));
            Assert.IsNotNull(cluster.HttpClient!.CaBundlePath);
            StringAssert.StartsWith(cluster.HttpClient.CaBundlePath, mount.TrimEnd('/', '\\') + "/backend-ca-");
            StringAssert.EndsWith(cluster.HttpClient.CaBundlePath, ".pem");
            var fileName = Path.GetFileName(cluster.HttpClient.CaBundlePath);
            Assert.IsTrue(result.InlineTlsFiles.ContainsKey(fileName));
            StringAssert.Contains(System.Text.Encoding.UTF8.GetString(result.InlineTlsFiles[fileName]), "BEGIN CERTIFICATE");
        }
        finally
        {
            try
            {
                Directory.Delete(mount, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [TestMethod]
    public void SubjectAltNames_AreUnsupported_AndLeaveClusterUntouched()
    {
        // Bug class: SAN validation is silently half-applied (hostname set, CA ignored).
        var b = Basic();
        b.BackendTlsPolicies.Add(BackendTls(
            "pol",
            "default",
            "web",
            "web.svc.cluster.local",
            wellKnownCa: "System",
            subjectAltNames: ["DNS:web.svc.cluster.local"]));
        var result = RenderWithPolicy(b, WebRoute());

        var cluster = result.Config.Clusters.Single(c => c.Id == "default/web:8080");
        Assert.IsTrue(cluster.Destinations.All(d => !d.UseHttps));
        Assert.IsNull(cluster.HttpClient);
    }

    [TestMethod]
    public void MissingHostname_SkipsPolicy()
    {
        var b = Basic();
        b.BackendTlsPolicies.Add(BackendTls("pol", "default", "web", hostname: null, wellKnownCa: "System"));
        var result = RenderWithPolicy(b, WebRoute());

        Assert.IsNull(result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
    }

    [TestMethod]
    public void MissingConfigMap_SkipsPolicy()
    {
        var b = Basic();
        b.BackendTlsPolicies.Add(BackendTls(
            "pol",
            "default",
            "web",
            "web.svc.cluster.local",
            caConfigMaps: ["missing-ca"]));
        var result = RenderWithPolicy(b, WebRoute());

        Assert.IsNull(result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
        Assert.AreEqual(0, result.InlineTlsFiles.Count);
    }

    [TestMethod]
    public void EmptyCaCrt_SkipsPolicy()
    {
        var b = Basic();
        b.ConfigMaps.Add(new ConfigMapInfo("default", "empty-ca", new Dictionary<string, string>
        {
            ["ca.crt"] = "   ",
        }));
        b.BackendTlsPolicies.Add(BackendTls(
            "pol",
            "default",
            "web",
            "web.svc.cluster.local",
            caConfigMaps: ["empty-ca"]));
        var result = RenderWithPolicy(b, WebRoute());

        Assert.IsNull(result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
    }

    [TestMethod]
    public void SecretKindCaRef_IsRejected()
    {
        // Bug class: Secret CA refs are treated as ConfigMaps and crash, or are silently ignored incorrectly.
        var b = Basic();
        var policy = BackendTls("pol", "default", "web", "web.svc.cluster.local");
        policy.Spec.Validation!.CaCertificateRefs =
        [
            new CaCertificateRef { Kind = GatewayApiNames.SecretKind, Name = "tls-secret" },
        ];
        b.BackendTlsPolicies.Add(policy);
        var result = RenderWithPolicy(b, WebRoute());

        Assert.IsNull(result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
    }

    [TestMethod]
    public void FirstPolicyWins_ByCreationTimestamp()
    {
        var b = Basic();
        b.BackendTlsPolicies.Add(BackendTls(
            "later",
            "default",
            "web",
            "later.example",
            wellKnownCa: "System",
            created: T0.AddMinutes(5)));
        b.BackendTlsPolicies.Add(BackendTls(
            "earlier",
            "default",
            "web",
            "earlier.example",
            wellKnownCa: "System",
            created: T0));
        var result = RenderWithPolicy(b, WebRoute());

        Assert.AreEqual(
            "earlier.example",
            result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient!.ServerName);
    }

    [TestMethod]
    public void NonServiceTargetRef_IsIgnored()
    {
        var b = Basic();
        var policy = BackendTls("pol", "default", "web", "web.svc.cluster.local", wellKnownCa: "System");
        policy.Spec.TargetRefs =
        [
            new BackendTlsTargetRef { Group = GatewayApiNames.Group, Kind = GatewayApiNames.HttpRouteKind, Name = "r" },
        ];
        b.BackendTlsPolicies.Add(policy);
        var result = RenderWithPolicy(b, WebRoute());

        Assert.IsNull(result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
    }
}
