using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: BackendTls CA namespace miss, empty CA refs, dual-service apply skip,
///     null policy timestamps, gRPC parent section miss, and HealthServer dispose after public ctor.
/// </summary>
[TestClass]
public class GatewayBackendTlsGrpcHealthSoftCoverageBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    private static string SelfSignedPem(string cn)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=" + cn,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return cert.ExportCertificatePem();
    }

    [TestMethod]
    public void BackendTls_ConfigMapWrongNamespace_AndEmptyCaRefs_SkipClusterTls()
    {
        var pem = SelfSignedPem("wrong-ns-ca");
        var b = Basic();
        b.ConfigMaps.Add(new ConfigMapInfo("apps", "ca", new Dictionary<string, string> { ["ca.crt"] = pem }));
        b.BackendTlsPolicies.Add(BackendTls(
            "pol",
            "default",
            "web",
            "web.svc",
            caConfigMaps: ["ca"]));
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var wrongNs = b.Render(BackendTlsOptions);
        AssertValid(wrongNs);
        Assert.IsNull(wrongNs.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
        Assert.AreEqual(0, wrongNs.InlineTlsFiles.Count);

        var emptyRefs = Basic();
        var policy = BackendTls("empty-refs", "default", "web", "web.svc");
        policy.Spec.Validation!.WellKnownCACertificates = null;
        policy.Spec.Validation.CaCertificateRefs = [];
        emptyRefs.BackendTlsPolicies.Add(policy);
        emptyRefs.Routes.Add(Route("r", "default", null, Rule("web")));
        var empty = emptyRefs.Render(BackendTlsOptions);
        AssertValid(empty);
        Assert.IsNull(empty.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
    }

    [TestMethod]
    public void BackendTls_OnlyMatchingService_GetsHttps_OthersStayCleartext()
    {
        var b = Basic();
        b.AddService("default", "api", 9090, "10.0.0.9");
        b.BackendTlsPolicies.Add(BackendTls("pol", "default", "web", "web.svc", wellKnownCa: "System"));
        b.Routes.Add(Route("web", "default", null, Rule("web")));
        b.Routes.Add(Route("api", "default", null, Rule("api", 9090)));
        var result = b.Render(BackendTlsOptions);
        AssertValid(result);

        var web = result.Config.Clusters.Single(c => c.Id == "default/web:8080");
        var api = result.Config.Clusters.Single(c => c.Id == "default/api:9090");
        Assert.AreEqual("web.svc", web.HttpClient!.ServerName);
        Assert.IsNull(api.HttpClient);
        Assert.IsTrue(api.Destinations.All(d => !d.UseHttps));
    }

    [TestMethod]
    public void BackendTls_NullCreationTimestamp_WinsOverLaterNamedPolicy()
    {
        var b = Basic();
        b.BackendTlsPolicies.Add(BackendTls(
            "z-last",
            "default",
            "web",
            "z.example",
            wellKnownCa: "System",
            created: null));
        b.BackendTlsPolicies.Add(BackendTls(
            "a-first",
            "default",
            "web",
            "a.example",
            wellKnownCa: "System",
            created: T0));
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render(BackendTlsOptions);
        AssertValid(result);
        Assert.AreEqual(
            "a.example",
            result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient!.ServerName);
    }

    [TestMethod]
    public void BackendTls_BareServiceTargetRef_AppliesSystemCa()
    {
        var b = Basic();
        var policy = BackendTls("pol", "default", "web", "bare.svc", wellKnownCa: "System");
        policy.Spec.TargetRefs = [new BackendTlsTargetRef { Name = "web" }];
        b.BackendTlsPolicies.Add(policy);
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render(BackendTlsOptions);
        AssertValid(result);
        Assert.AreEqual("bare.svc", result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient!.ServerName);
    }

    [TestMethod]
    public void GrpcRoute_SectionMiss_AndForeignParentOnly_AreRejectedOrIgnored()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Http("http", 80)];
        var miss = GrpcRoute("miss", "default", null, GrpcRule("web"));
        miss.Spec.ParentRefs = [ParentGw(section: "https")];
        b.GrpcRoutes.Add(miss);
        var missResult = b.Render();
        AssertValid(missResult);
        Assert.AreEqual("False", Cond(Parent(missResult, StatusKind.GrpcRoute, "miss").Conditions, "Accepted").Status);
        Assert.AreEqual("NoMatchingParent", Cond(Parent(missResult, StatusKind.GrpcRoute, "miss").Conditions, "Accepted").Reason);

        var foreign = Basic();
        var route = GrpcRoute("r", "default", null, GrpcRule("web"));
        route.Spec.ParentRefs =
        [
            new ParentReference { Name = "gw", Group = "example.com", Kind = GatewayApiNames.GatewayKind },
        ];
        foreign.GrpcRoutes.Add(route);
        var foreignResult = foreign.Render();
        AssertValid(foreignResult);
        Assert.IsFalse(foreignResult.StatusPatches.Any(p => p.Kind == StatusKind.GrpcRoute));
        Assert.AreEqual(0, foreignResult.Config.UserRoutes().Count);
    }

    [TestMethod]
    public void HealthServer_PublicCtor_DisposeWithoutRun()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var server = new HealthServer(port, () => true, NullLogger.Instance);
        server.Dispose();
        Assert.IsNotNull(server);
    }
}
