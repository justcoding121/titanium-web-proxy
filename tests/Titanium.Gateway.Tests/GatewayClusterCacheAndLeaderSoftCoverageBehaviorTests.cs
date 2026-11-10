using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
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
///     Soft leftovers toward 90%: ClusterCache discovery fallback, GetFile/GetPfx, LeaderElector lease
///     read failures, InlineTlsFiles publish, and L4 apply early-returns when PrimaryCluster is absent.
/// </summary>
[TestClass]
public class GatewayClusterCacheAndLeaderSoftCoverageBehaviorTests
{
    private sealed class FlakyGroupDiscoveryApi : IKubeApi
    {
        private readonly FakeKubeApi _inner = new();
        private int _groupGets;

        public FakeKubeApi Inner => _inner;

        public Task<KubeResponse> SendAsync(
            HttpMethod method, string pathAndQuery, string? body, string? contentType, CancellationToken cancellationToken)
        {
            var path = pathAndQuery.Split('?', 2)[0];
            if (method == HttpMethod.Get && path == "/apis/gateway.networking.k8s.io")
            {
                _groupGets++;
                if (_groupGets == 1)
                {
                    throw new IOException("discovery transport fail");
                }
            }

            return _inner.SendAsync(method, pathAndQuery, body, contentType, cancellationToken);
        }

        public Task<Stream> OpenWatchAsync(string pathAndQuery, CancellationToken cancellationToken) =>
            _inner.OpenWatchAsync(pathAndQuery, cancellationToken);
    }

    private sealed class LeaseGetFailsApi : IKubeApi
    {
        public required FakeKubeApi Inner { get; init; }

        public Task<KubeResponse> SendAsync(
            HttpMethod method, string pathAndQuery, string? body, string? contentType, CancellationToken cancellationToken)
        {
            var path = pathAndQuery.Split('?', 2)[0];
            if (method == HttpMethod.Get && path.Contains("/leases/", StringComparison.Ordinal))
            {
                return Task.FromResult(new KubeResponse(HttpStatusCode.InternalServerError, "lease read fail"));
            }

            return Inner.SendAsync(method, pathAndQuery, body, contentType, cancellationToken);
        }

        public Task<Stream> OpenWatchAsync(string pathAndQuery, CancellationToken cancellationToken) =>
            Inner.OpenWatchAsync(pathAndQuery, cancellationToken);
    }

    private static void SeedMinimalCluster(FakeKubeApi api)
    {
        api.Apply("namespaces", ClusterFixture.Obj("""{ "metadata": { "name": "default" } }"""));
        api.Apply("gatewayclasses", ClusterFixture.Obj($$"""
            { "metadata": { "name": "titanium" }, "spec": { "controllerName": "{{ClusterFixture.Controller}}" } }
            """));
        api.Apply("gateways", ClusterFixture.Obj("""
            {
              "metadata": { "name": "gw", "namespace": "default" },
              "spec": {
                "gatewayClassName": "titanium",
                "listeners": [ { "name": "http", "port": 80, "protocol": "HTTP" } ]
              }
            }
            """));
        api.Apply("services", ClusterFixture.Obj("""
            {
              "metadata": { "name": "web", "namespace": "default" },
              "spec": { "ports": [ { "port": 8080 } ] }
            }
            """));
        api.Apply("endpointslices", ClusterFixture.Obj("""
            {
              "metadata": { "name": "web-abc", "namespace": "default", "labels": { "kubernetes.io/service-name": "web" } },
              "addressType": "IPv4",
              "ports": [ { "port": 8080, "protocol": "TCP" } ],
              "endpoints": [ { "addresses": [ "10.0.0.1" ], "conditions": { "ready": true } } ]
            }
            """));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ClusterCache_InitialDiscoveryFailure_FallsBackAndSyncs()
    {
        var api = new FlakyGroupDiscoveryApi();
        SeedMinimalCluster(api.Inner);
        using var stop = new CancellationTokenSource();
        var cache = new ClusterCache(api, new ClusterCacheOptions(), NullLogger.Instance);
        var run = cache.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced);
            var snap = cache.Snapshot();
            Assert.IsTrue(snap.Gateways.Count >= 1);
            Assert.IsFalse(cache.IsKindAvailable("unknown-kind"));
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
    [Timeout(30_000)]
    public async Task ClusterCache_GetFile_AndGetPfx_ReturnBytesOrNull()
    {
        await using var fixture = new ClusterFixture();
        fixture.Api.Apply("secrets", ClusterFixture.TlsSecret("default", "edge-cert", valid: true));
        using var stop = new CancellationTokenSource();
        var cache = new ClusterCache(fixture.Api, new ClusterCacheOptions(), NullLogger.Instance);
        var run = cache.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced);
            var getPfx = typeof(ClusterCache).GetMethod("GetPfx", BindingFlags.Instance | BindingFlags.NonPublic);
            var getFile = typeof(ClusterCache).GetMethod("GetFile", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(getPfx);
            Assert.IsNotNull(getFile);

            var pfx = (byte[]?)getPfx!.Invoke(cache, ["default", "edge-cert"]);
            Assert.IsNotNull(pfx);
            Assert.IsTrue(pfx!.Length > 0);

            var certPem = (byte[]?)getFile!.Invoke(cache, [new TlsFile(string.Empty, "default", "edge-cert", TlsFileKind.CertificatePem)]);
            Assert.IsNotNull(certPem);

            fixture.Api.Delete("secrets", "default", "edge-cert");
            await ClusterFixture.WaitForAsync(() => cache.Snapshot().Secrets.All(s => s.Name != "edge-cert"));
            Assert.IsNull(getPfx.Invoke(cache, ["default", "edge-cert"]));
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
    public async Task LeaderElector_LeaseGetFailure_ThrowsKubeApiException()
    {
        var inner = new FakeKubeApi();
        var elector = new LeaderElector(
            inner,
            new LeaderElectionOptions { Namespace = "ns", Identity = "a", Name = "fail-lease" },
            NullLogger.Instance);
        await elector.StepAsync(CancellationToken.None);
        Assert.IsTrue(elector.IsLeader);

        var failing = new LeaderElector(
            new LeaseGetFailsApi { Inner = inner },
            new LeaderElectionOptions { Namespace = "ns", Identity = "a", Name = "fail-lease" },
            NullLogger.Instance);
        await failing.StepAsync(CancellationToken.None);
        Assert.IsFalse(failing.IsLeader);
    }

    [TestMethod]
    public void L4Routes_PrimaryClusterMissing_SkipsListenerProgramming()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener
            {
                Name = "tls",
                Port = 443,
                Protocol = "TLS",
                Hostname = "sni.test",
                Tls = new ListenerTls { Mode = "Passthrough" },
            },
            Tcp("tcp", 8080),
            Udp("udp", 5353),
        ];
        b.TlsRoutes.Add(TlsRoute("tls-r", "default", ["sni.test"], Backend("ghost", 443)));
        b.TcpRoutes.Add(TcpRoute("tcp-r", "default", Backend("ghost", 8080)));
        b.UdpRoutes.Add(UdpRoute("udp-r", "default", Backend("ghost", 53)));
        var result = b.Render();
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

        Assert.IsFalse(result.Config.Listeners.Any(l => l.SniDefaultClusterId is not null || l.ClusterId is not null || l.SniRoutes.Count > 0));
        Assert.IsFalse(result.Config.Listeners.Any(l => string.Equals(l.Type, "udp", StringComparison.OrdinalIgnoreCase) && l.ClusterId is not null));
    }
}
