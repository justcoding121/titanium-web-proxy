using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Control;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: LeaderElector leadership logging, ingress canary with no endpoints,
///     invalid canary-by-header, gRPC attach when HTTP-only kinds, TCP zero-weight backends,
///     TLS passthrough SNI default, certificate chain concat, and UDP default SupportedKinds.
/// </summary>
[TestClass]
public class GatewayIngressL4GrpcSoftCoverageBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static string A(string name) => IngressAnnotations.Prefix + name;

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public async Task LeaderElector_LeadershipTransitions_RaiseEvent()
    {
        var log = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information));
        var api = new FakeKubeApi();
        var elector = new LeaderElector(
            api,
            new LeaderElectionOptions { Namespace = "ns", Identity = "soft-a", Name = "soft-lease" },
            log.CreateLogger<LeaderElector>());
        var changes = new List<bool>();
        elector.LeadershipChanged += changes.Add;
        await elector.StepAsync(CancellationToken.None);
        Assert.IsTrue(elector.IsLeader);
        Assert.AreEqual(1, changes.Count);
        Assert.IsTrue(changes[0]);

        var lease = api.Get("leases", "ns", "soft-lease")!;
        lease["spec"]!["holderIdentity"] = "other";
        api.Apply("leases", lease);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await elector.RunAsync(stop.Token);
        Assert.IsFalse(elector.IsLeader);
        Assert.IsTrue(changes.Any(static c => !c));
    }

    [TestMethod]
    public void IngressAnnotations_CanaryByHeader_InvalidHeader_IsRejected()
    {
        var bad = IngressAnnotations.Parse(new Dictionary<string, string>
        {
            [A("canary")] = "true",
            [A("canary-by-header")] = "bad header",
        }, "default");
        Assert.IsTrue(bad.Problems.Exists(static p => p.Contains("header name", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Ingress_CanaryWithNoEndpoints_PrimaryOnly_NoCanaryClusters()
    {
        var b = IngressOnly();
        b.AddService("default", "canary", 8080);
        b.Slices.RemoveAll(s => s.ServiceName == "canary");
        b.Ingresses.Add(Ingress("main", "titanium", [IRule("app.test", IPath("/shop", "Prefix"))]));
        b.Ingresses.Add(Ingress(
            "canary",
            "titanium",
            [IRule("app.test", IPath("/shop", "Prefix", "canary", 8080))],
            new Dictionary<string, string>
            {
                [A("canary")] = "true",
                [A("canary-weight")] = "25",
            },
            created: T0.AddMinutes(5)));

        var result = b.Render();
        AssertValid(result);

        var main = result.Config.UserRoutes().Single(r => r.Id.Contains("/main/", StringComparison.Ordinal));
        Assert.AreEqual("default/web:8080", main.ClusterId);
        Assert.IsNull(main.Clusters);
    }

    [TestMethod]
    public void GrpcRoute_HttpOnlyAllowedKinds_IsNotAccepted()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners[0].AllowedRoutes = new AllowedRoutes
        {
            Kinds = [new RouteGroupKind { Group = GatewayApiNames.Group, Kind = GatewayApiNames.HttpRouteKind }],
        };
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            GrpcRule(
                "web",
                matches: [new GrpcRouteMatch { Method = new GrpcMethodMatch { Service = "pkg.Echo", Method = "Say" } }])));
        var result = b.Render();
        AssertValid(result);

        var parent = result.StatusPatches.Single(p => p.Kind == StatusKind.GrpcRoute).Parents!.Single();
        Assert.AreEqual("False", Cond(parent.Conditions, "Accepted").Status);
        Assert.AreEqual(0, result.Config.UserRoutes().Count);
    }

    [TestMethod]
    public void TcpRoute_OnlyZeroWeightBackend_HasNoCluster()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        var zero = Backend("web");
        zero.Weight = 0;
        var route = TcpRoute("r", "default", zero);
        b.TcpRoutes.Add(route);
        var result = b.Render();
        AssertValid(result);

        var parent = result.StatusPatches.Single(p => p.Kind == StatusKind.TcpRoute).Parents!.Single();
        var refs = Cond(parent.Conditions, "ResolvedRefs");
        Assert.AreEqual("False", refs.Status);
        Assert.AreEqual(0, result.Config.Clusters.Count(c => c.Id.Contains("web", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TlsPassthrough_EmptyListenerHostname_SetsSniDefaultCluster()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [TlsPassthrough("tls", 443)];
        b.TlsRoutes.Add(TlsRoute("r", "default", null, Backend("web")));
        var result = b.Render();
        AssertValid(result);

        var listener = result.Config.Listeners.Single(l => l.Passthrough);
        Assert.AreEqual("default/web:8080", listener.SniDefaultClusterId);
        Assert.AreEqual(0, listener.SniRoutes.Count);
    }

    [TestMethod]
    public void CertificateStore_UnknownTlsFileKind_ReturnsPfx()
    {
        var store = new CertificateStore();
        var (keyPem, certPem) = LeafPem("leaf.example");
        var secret = new SecretData("ns", "leaf", "kubernetes.io/tls", "1", certPem, keyPem);
        var pfx = store.GetPfx(secret);
        Assert.IsNotNull(pfx);
        Assert.AreSame(pfx, store.GetPem(secret, (TlsFileKind)byte.MaxValue));
    }

    [TestMethod]
    public void UdpListener_DefaultAllowedKinds_ListsUdpRouteKind()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Udp("dns", 53)];
        b.AddUdpService("default", "dns", 53, "10.0.0.9");
        b.UdpRoutes.Add(UdpRoute("r", "default", Backend("dns", 53)));
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.IsTrue(listener.SupportedKinds.Any(k => k.Kind == GatewayApiNames.UdpRouteKind));
        Assert.AreEqual("True", Cond(Parent(result, StatusKind.UdpRoute).Conditions, "Accepted").Status);
    }

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static (byte[] KeyPem, byte[] CertPem) LeafPem(string dns)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + dns, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dns);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (
            Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()),
            Encoding.UTF8.GetBytes(cert.ExportCertificatePem()));
    }
}
