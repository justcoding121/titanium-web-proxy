using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: negative GatewayOptions validation, CertificateStore multi-cert PFX,
///     ClusterCache Snapshot with optional route kinds, and L4 apply early-returns when no cluster exists.
/// </summary>
[TestClass]
public class GatewayClusterSnapshotAndTlsSoftCoverageBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void GatewayOptions_NegativeMinReconcile_Throws()
    {
        var options = GatewayOptions.Parse(
            ["--data-plane-service=titanium", "--data-plane-namespace=gw", "--no-leader-election"],
            new Dictionary<string, string?> { ["POD_NAMESPACE"] = "gw", ["POD_NAME"] = "pod" });
        typeof(GatewayOptions).GetProperty(nameof(GatewayOptions.MinReconcileMilliseconds))!
            .SetValue(options, -5);
        var validate = typeof(GatewayOptions).GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic);
        var ex = Assert.ThrowsExactly<TargetInvocationException>(() => validate!.Invoke(options, null));
        StringAssert.Contains(ex.InnerException!.Message, "must not be negative");
    }

    [TestMethod]
    public void CertificateStore_CaPemImportFailure_ReturnsFalseFromHasCa()
    {
        var store = new CertificateStore();
        var secret = new SecretData(
            "ns",
            "bad-ca",
            "kubernetes.io/tls",
            "1",
            null,
            null,
            Encoding.UTF8.GetBytes("-----BEGIN CERTIFICATE-----\nnot-valid-base64!!!\n-----END CERTIFICATE-----"));
        Assert.IsFalse(store.HasCa(secret));
        Assert.IsNull(store.GetPem(secret, TlsFileKind.CaPem));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ClusterCache_Snapshot_IncludesOptionalRouteKindsAndSecretUsability()
    {
        await using var fixture = new ClusterFixture();
        fixture.Api.Apply("tlsroutes", ClusterFixture.Obj("""
            {
              "metadata": { "name": "tls-r", "namespace": "default" },
              "spec": {
                "parentRefs": [ { "name": "gw", "namespace": "default" } ],
                "hostnames": [ "tls.test" ],
                "rules": [ { "backendRefs": [ { "name": "web", "port": 8080 } ] } ]
              }
            }
            """));
        fixture.Api.Apply("tcproutes", ClusterFixture.Obj("""
            {
              "metadata": { "name": "tcp-r", "namespace": "default" },
              "spec": {
                "parentRefs": [ { "name": "gw", "namespace": "default" } ],
                "rules": [ { "backendRefs": [ { "name": "web", "port": 8080 } ] } ]
              }
            }
            """));
        fixture.Api.Apply("udproutes", ClusterFixture.Obj("""
            {
              "metadata": { "name": "udp-r", "namespace": "default" },
              "spec": {
                "parentRefs": [ { "name": "gw", "namespace": "default" } ],
                "rules": [ { "backendRefs": [ { "name": "web", "port": 8080 } ] } ]
              }
            }
            """));
        fixture.Api.Apply("grpcroutes", ClusterFixture.Obj("""
            {
              "metadata": { "name": "grpc-r", "namespace": "default" },
              "spec": {
                "parentRefs": [ { "name": "gw", "namespace": "default" } ],
                "rules": [ { "matches": [ { "method": { "type": "Exact", "service": "pkg.Echo", "method": "Say" } } ],
                  "backendRefs": [ { "name": "web", "port": 8080 } ] } ]
              }
            }
            """));
        fixture.Api.Apply("gateways", ClusterFixture.Obj("""
            {
              "metadata": { "name": "gw", "namespace": "default" },
              "spec": {
                "gatewayClassName": "titanium",
                "listeners": [
                  { "name": "http", "port": 80, "protocol": "HTTP", "allowedRoutes": { "namespaces": { "from": "All" } } },
                  { "name": "tls", "port": 443, "protocol": "TLS", "tls": { "mode": "Passthrough" } },
                  { "name": "tcp", "port": 8080, "protocol": "TCP" },
                  { "name": "udp", "port": 5353, "protocol": "UDP" }
                ]
              }
            }
            """));
        fixture.Api.Apply("secrets", ClusterFixture.TlsSecret("default", "edge-tls", valid: true));

        var cache = new ClusterCache(fixture.Api, new ClusterCacheOptions(), NullLogger.Instance);
        using var stop = new CancellationTokenSource();
        var run = cache.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced);
            var snapshot = cache.Snapshot();
            Assert.IsTrue(snapshot.TlsRoutes.Count >= 1);
            Assert.IsTrue(snapshot.TcpRoutes.Count >= 1);
            Assert.IsTrue(snapshot.UdpRoutes.Count >= 1);
            Assert.IsTrue(snapshot.GrpcRoutes.Count >= 1);
            Assert.IsTrue(snapshot.Secrets.Any(s => s.Name == "edge-tls" && s.HasCertificate));
        }
        finally
        {
            stop.Cancel();
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [TestMethod]
    public void TlsRoute_PrimaryClusterMissing_DoesNotAttachSniRoute()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener
            {
                Name = "tls",
                Port = 443,
                Protocol = "TLS",
                Hostname = "edge.test",
                Tls = new ListenerTls { Mode = "Passthrough" },
            },
        ];
        b.TlsRoutes.Add(TlsRoute("r", "default", ["edge.test"], Backend("missing-svc", 443)));
        var result = b.Render();
        AssertValid(result);
        Assert.AreEqual("False", Cond(Parent(result, StatusKind.TlsRoute).Conditions, "ResolvedRefs").Status);
        var listener = result.Config.Listeners.SingleOrDefault(l => l.Passthrough);
        Assert.IsNull(listener?.SniDefaultClusterId);
        Assert.AreEqual(0, listener?.SniRoutes.Count ?? 0);
    }

    [TestMethod]
    public void TcpRoute_UnresolvedBackend_LeavesListenerWithoutDefaultCluster()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        b.TcpRoutes.Add(TcpRoute("r", "default", Backend("ghost", 8080)));
        var result = b.Render();
        AssertValid(result);
        Assert.AreEqual("False", Cond(Parent(result, StatusKind.TcpRoute).Conditions, "ResolvedRefs").Status);
        Assert.IsFalse(result.Config.Listeners.Any(l => l.Passthrough && l.SniDefaultClusterId is not null));
    }

    [TestMethod]
    public void UdpRoute_UnresolvedBackend_SkipsUdpListenerCluster()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Udp("udp", 53)];
        b.UdpRoutes.Add(UdpRoute("r", "default", Backend("ghost", 53)));
        var result = b.Render();
        AssertValid(result);
        Assert.AreEqual("False", Cond(Parent(result, StatusKind.UdpRoute).Conditions, "ResolvedRefs").Status);
        Assert.IsFalse(result.Config.Listeners.Any(l => string.Equals(l.Type, "udp", StringComparison.OrdinalIgnoreCase) && l.ClusterId is not null));
    }

}
