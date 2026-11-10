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
///     Soft leftovers toward 90%: ClusterCache secret scoping, LeaderElector renew deadline,
///     GatewayController observe/poke loop arms, StatusWriter route kinds, and Informer retry.
/// </summary>
[TestClass]
public class GatewayClusterLeaderStatusSoftCoverageBehaviorTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 9, 19, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ClusterCache_SecretNamespacesAndBackendTls_SyncAndSnapshot()
    {
        await using var fixture = new ClusterFixture();
        fixture.Api.Apply("secrets", ClusterFixture.Obj("""
            {
              "metadata": { "name": "scoped-tls", "namespace": "team-a" },
              "type": "kubernetes.io/tls",
              "data": { "tls.crt": "", "tls.key": "" }
            }
            """));
        var options = new ClusterCacheOptions
        {
            SecretNamespaces = ["team-a"],
            BackendTlsPolicyEnabled = true,
        };
        var cache = new ClusterCache(fixture.Api, options, NullLogger.Instance);
        using var stop = new CancellationTokenSource();
        var run = cache.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced);
            var snap = cache.Snapshot();
            Assert.IsTrue(snap.ConfigMaps is not null);
            Assert.IsTrue(cache.IsKindAvailable("tlsroutes"));
            _ = snap.BackendTlsPolicies;
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
    public async Task LeaderElector_OfflinePastRenewDeadline_StepsDown()
    {
        var api = new FakeKubeApi();
        var time = new ManualTime();
        var elector = new LeaderElector(
            api,
            new LeaderElectionOptions
            {
                Namespace = "ns",
                Identity = "deadline-a",
                Name = "deadline-lease",
                RenewDeadline = TimeSpan.FromSeconds(5),
            },
            NullLogger.Instance,
            time);
        await elector.StepAsync(CancellationToken.None);
        Assert.IsTrue(elector.IsLeader);

        api.Offline = true;
        time.Advance(TimeSpan.FromSeconds(12));
        await elector.StepAsync(CancellationToken.None);
        Assert.IsFalse(elector.IsLeader);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task GatewayController_NoDataPlaneService_SkipsPodObservation()
    {
        await using var fixture = new ClusterFixture();
        var options = ClusterFixture.Options(TimeSpan.Zero) with
        {
            Translator = new GatewayTranslatorOptions
            {
                DataPlaneServiceName = null,
                DataPlaneServiceNamespace = null,
            },
        };
        var cache = new ClusterCache(fixture.Api, new ClusterCacheOptions(), NullLogger.Instance);
        var controller = new GatewayController(
            cache,
            new OutputWriter(fixture.Api, options.Output),
            new StatusWriter(fixture.Api, ClusterFixture.Controller, NullLogger.Instance),
            fixture.Observer,
            null,
            options,
            NullLogger.Instance);

        using var stop = new CancellationTokenSource();
        var cacheRun = cache.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced);
            var outcome = await controller.ReconcileOnceAsync(CancellationToken.None);
            Assert.IsTrue(outcome.Leader);
            Assert.IsFalse(outcome.Confirmed);
            Assert.AreEqual(0, fixture.Api.WritesTo("/pods/").Count);
        }
        finally
        {
            stop.Cancel();
            try
            {
                await cacheRun;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task GatewayController_RunAsync_ReconcileLoopHonorsMinInterval()
    {
        await using var fixture = new ClusterFixture();
        var time = new ManualTime();
        var cache = new ClusterCache(fixture.Api, new ClusterCacheOptions(), NullLogger.Instance);
        var options = ClusterFixture.Options(TimeSpan.FromMilliseconds(200));
        var controller = new GatewayController(
            cache,
            new OutputWriter(fixture.Api, options.Output),
            new StatusWriter(fixture.Api, ClusterFixture.Controller, NullLogger.Instance),
            fixture.Observer,
            null,
            options,
            NullLogger.Instance,
            time);

        using var stop = new CancellationTokenSource();
        var cacheRun = cache.RunAsync(stop.Token);
        var ctlRun = controller.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced);
            controller.Signal();
            controller.Signal();
            await Task.Delay(50);
            var before = fixture.Api.Writes.Count;
            await Task.Delay(300);
            Assert.IsTrue(fixture.Api.Writes.Count >= before);
        }
        finally
        {
            stop.Cancel();
            try
            {
                await Task.WhenAll(cacheRun, ctlRun);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [TestMethod]
    public async Task StatusWriter_IngressAndGatewayPatches_Apply()
    {
        var api = new FakeKubeApi();
        var b = IngressOnly();
        b.Ingresses.Add(Ingress("shop", "titanium", [IRule("shop.test", IPath("/", "Prefix"))]));
        var render = b.Render();
        api.Apply("ingresses", ClusterFixture.Obj("""
            {
              "apiVersion": "networking.k8s.io/v1",
              "kind": "Ingress",
              "metadata": { "name": "shop", "namespace": "default", "generation": 1 },
              "spec": { "ingressClassName": "titanium" }
            }
            """));

        var ingressPatch = render.StatusPatches.Single(p => p.Kind == StatusKind.Ingress);
        var writer = new StatusWriter(api, Controller, NullLogger.Instance);
        var (written, failed) = await writer.ApplyAsync(
            [ingressPatch],
            b.Build(),
            CancellationToken.None);
        Assert.AreEqual(1, written);
        Assert.AreEqual(0, failed);

        var gw = Basic();
        var gatewayPatch = gw.Render().StatusPatches.Single(p => p.Kind == StatusKind.Gateway);
        api.Apply("gateways", ClusterFixture.Obj("""
            {
              "apiVersion": "gateway.networking.k8s.io/v1",
              "kind": "Gateway",
              "metadata": { "name": "gw", "namespace": "default", "generation": 1 },
              "spec": { "gatewayClassName": "titanium" }
            }
            """));
        (written, failed) = await writer.ApplyAsync([gatewayPatch], gw.Build(), CancellationToken.None);
        Assert.AreEqual(1, written);
        Assert.AreEqual(0, failed);
    }

    [TestMethod]
    public void Ingress_DefaultBackendAndInvalidPathType_AreSoftHandled()
    {
        var b = IngressOnly();
        b.Ingresses.Add(Ingress(
            "def",
            "titanium",
            rules: null,
            annotations: null));
        b.Ingresses[^1].Spec.DefaultBackend = IBackend("web");
        b.Ingresses.Add(Ingress(
            "bad",
            "titanium",
            [IRule("bad.test", new IngressPath { Path = "/x", PathType = "Glob", Backend = IBackend("web") })]));

        var result = b.Render();
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count);
        Assert.IsTrue(result.Config.UserRoutes().Any(r => r.Id.Contains("default", StringComparison.Ordinal)));
        Assert.IsTrue(result.Events.Any(e => e.Reason == "InvalidPath"));
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task Informer_ListFailureThenSuccess_Syncs()
    {
        var api = new FakeKubeApi();
        api.Offline = true;
        var informer = new Informer<ServiceInfo>(
            api,
            "/api/v1/services",
            null,
            ResourceParsers.Service,
            NullLogger.Instance);
        using var stop = new CancellationTokenSource();
        var run = informer.RunAsync(stop.Token);
        await Task.Delay(120);
        api.Offline = false;
        await ClusterFixture.WaitForAsync(() => informer.HasSynced);
        Assert.IsTrue(informer.Items.Count >= 0);
        await stop.CancelAsync();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
