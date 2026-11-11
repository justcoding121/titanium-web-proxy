using System.Net;
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
///     Soft leftovers toward 90%: GatewayApiDiscovery status arms, LeaderElector Put conflict,
///     GatewayController Ready/Signal, and TLS Terminate listener soft path.
/// </summary>
[TestClass]
public class GatewayDiscoveryLeaderAndControllerSoftCoverageBehaviorTests
{
    private sealed class MapApi : IKubeApi
    {
        private readonly Dictionary<string, Func<KubeResponse>> _gets = new(StringComparer.Ordinal);

        public void OnGet(string path, Func<KubeResponse> response) => _gets[path] = response;

        public Task<KubeResponse> SendAsync(
            HttpMethod method, string pathAndQuery, string? body, string? contentType, CancellationToken cancellationToken)
        {
            var path = pathAndQuery.Split('?', 2)[0];
            if (method == HttpMethod.Get && _gets.TryGetValue(path, out var factory))
            {
                return Task.FromResult(factory());
            }

            return Task.FromResult(new KubeResponse(HttpStatusCode.NotFound, string.Empty));
        }

        public Task<Stream> OpenWatchAsync(string pathAndQuery, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream());
    }

    private sealed class ConflictOnPutApi : IKubeApi
    {
        private readonly IKubeApi _inner;
        public int PutConflicts { get; set; } = 1;

        public ConflictOnPutApi(IKubeApi inner) => _inner = inner;

        public async Task<KubeResponse> SendAsync(
            HttpMethod method, string pathAndQuery, string? body, string? contentType, CancellationToken cancellationToken)
        {
            if (method == HttpMethod.Put && PutConflicts > 0)
            {
                PutConflicts--;
                return new KubeResponse(HttpStatusCode.Conflict, "conflict");
            }

            return await _inner.SendAsync(method, pathAndQuery, body, contentType, cancellationToken);
        }

        public Task<Stream> OpenWatchAsync(string pathAndQuery, CancellationToken cancellationToken) =>
            _inner.OpenWatchAsync(pathAndQuery, cancellationToken);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [TestMethod]
    public async Task GatewayApiDiscovery_NotFoundErrorEmptyAndServedVersions()
    {
        var missing = new MapApi();
        missing.OnGet("/apis/gateway.networking.k8s.io", () => new KubeResponse(HttpStatusCode.NotFound, ""));
        var absent = await new GatewayApiDiscovery(missing, NullLogger.Instance).DiscoverAsync(CancellationToken.None);
        Assert.IsNull(absent.TlsRoutes);

        var errored = new MapApi();
        errored.OnGet("/apis/gateway.networking.k8s.io", () => new KubeResponse(HttpStatusCode.InternalServerError, "boom"));
        var fromError = await new GatewayApiDiscovery(errored, NullLogger.Instance).DiscoverAsync(CancellationToken.None);
        Assert.IsNull(fromError.GrpcRoutes);

        var empty = new MapApi();
        empty.OnGet("/apis/gateway.networking.k8s.io", () => new KubeResponse(HttpStatusCode.OK, """{"versions":[]}"""));
        var noVersions = await new GatewayApiDiscovery(empty, NullLogger.Instance).DiscoverAsync(CancellationToken.None);
        Assert.IsNull(noVersions.TcpRoutes);

        var ok = new MapApi();
        ok.OnGet("/apis/gateway.networking.k8s.io", () => new KubeResponse(HttpStatusCode.OK, """
            {"versions":[{"version":"v1"},{"version":"v1alpha2"}]}
            """));
        ok.OnGet("/apis/gateway.networking.k8s.io/v1", () => new KubeResponse(HttpStatusCode.OK, """
            {"resources":[{"name":"tlsroutes"},{"name":"grpcroutes"},{"name":"backendtlspolicies"}]}
            """));
        ok.OnGet("/apis/gateway.networking.k8s.io/v1alpha2", () => new KubeResponse(HttpStatusCode.OK, """
            {"resources":[{"name":"tcproutes"},{"name":"udproutes"}]}
            """));
        var served = await new GatewayApiDiscovery(ok, NullLogger.Instance).DiscoverAsync(CancellationToken.None);
        Assert.AreEqual("v1", served.TlsRoutes);
        Assert.AreEqual("v1", served.GrpcRoutes);
        Assert.AreEqual("v1", served.BackendTlsPolicies);
        Assert.AreEqual("v1alpha2", served.TcpRoutes);
        Assert.AreEqual("v1alpha2", served.UdpRoutes);
        Assert.AreEqual("v1", served.PathFor("tlsroutes"));
        Assert.IsNull(served.PathFor("unknown"));
        Assert.IsNull(GatewayApiVersions.Absent.PathFor("tlsroutes"));
    }

    [TestMethod]
    public async Task LeaderElector_PutConflict_ReturnsFalseWithoutThrowing()
    {
        var inner = new FakeKubeApi();
        var time = new ManualTime();
        var first = new LeaderElector(
            inner,
            new LeaderElectionOptions { Namespace = "ns", Identity = "a" },
            NullLogger.Instance,
            time);
        await first.StepAsync(CancellationToken.None);
        Assert.IsTrue(first.IsLeader);

        var conflict = new ConflictOnPutApi(inner) { PutConflicts = 1 };
        var renewer = new LeaderElector(
            conflict,
            new LeaderElectionOptions { Namespace = "ns", Identity = "a" },
            NullLogger.Instance,
            time);
        // Seed observed state by reading the existing lease once through a non-conflicting step first.
        await renewer.StepAsync(CancellationToken.None);
        // Next renew Put conflicts.
        conflict.PutConflicts = 1;
        time.Advance(TimeSpan.FromSeconds(2));
        await renewer.StepAsync(CancellationToken.None);
        // Conflict is soft: StepAsync must not throw; Put failure clears leadership for this identity.
        Assert.IsFalse(renewer.IsLeader,
            "Conflicting renew Put returns false without throwing and drops IsLeader.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task GatewayController_ReadySignalAndIsLeader_WithoutElection()
    {
        await using var fixture = new ClusterFixture();
        await fixture.StartCacheAsync();
        Assert.IsTrue(fixture.Reconciler.Ready);
        Assert.IsTrue(fixture.Reconciler.IsLeader);
        fixture.Reconciler.Signal();
        var outcome = await fixture.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.IsTrue(outcome.Leader);
        Assert.IsTrue(outcome.HasListeners || outcome.WroteOutput || outcome.Confirmed);
    }

    [TestMethod]
    public void TlsTerminateListener_WithSecret_ProgramsTerminate()
    {
        // TLS Terminate + TCPRoute programs TerminateTls on the mapped data-plane port (443→8443).
        var b = Basic();
        b.Secrets.Add(new SecretInfo("default", "term-cert", "kubernetes.io/tls", true, true));
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener
            {
                Name = "tls-term",
                Port = 443,
                Protocol = "TLS",
                Tls = new ListenerTls
                {
                    Mode = "Terminate",
                    CertificateRefs = [new SecretObjectReference { Name = "term-cert" }],
                },
            },
        ];
        b.TcpRoutes.Add(TcpRoute("r", "default", Backend("web")));
        var result = b.Render();
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

        var gatewayListener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("True", gatewayListener.Conditions.Single(c => c.Type == "Accepted").Status);
        var programmed = result.Config.Listeners.Single(l => l.Port == 8443);
        Assert.IsTrue(programmed.TerminateTls);
        Assert.IsTrue(result.TlsFiles.Any(f => f.SecretName == "term-cert"));
    }
}
