using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json.Nodes;
using Titanium.Gateway.Control;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Gateway.Tests;

/// <summary>Ingress through the whole controller: informers, the published ConfigMap, status, and events.</summary>
[TestClass]
public sealed class IngressControllerTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static void SeedIngress(ClusterFixture f, string name = "shop", string host = "shop.test", string service = "web", string? annotations = null)
    {
        f.Api.Apply("ingressclasses", ClusterFixture.Obj($$"""{ "metadata": { "name": "titanium" }, "spec": { "controller": "titanium.io/ingress-controller" } }"""));
        f.Api.Apply("ingresses", ClusterFixture.Obj($$"""
            {
              "metadata": { "name": "{{name}}", "namespace": "default", "uid": "uid-{{name}}", "annotations": { {{annotations}} } },
              "spec": {
                "ingressClassName": "titanium",
                "rules": [ { "host": "{{host}}", "http": { "paths": [ { "path": "/", "pathType": "Prefix", "backend": { "service": { "name": "{{service}}", "port": { "number": 8080 } } } } ] } } ]
              }
            }
            """));
    }

    private static async Task SettleAsync(ClusterFixture f, int ingresses)
    {
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().Ingresses.Count == ingresses && f.Cache.Snapshot().IngressClasses.Count >= 1);
    }

    [TestMethod]
    public async Task AnIngress_BecomesARoute_AndGetsTheLoadBalancerAddress()
    {
        await using var f = new ClusterFixture();
        SeedIngress(f);
        await f.StartCacheAsync();
        await SettleAsync(f, 1);

        var outcome = await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.IsTrue(outcome.Confirmed);
        var config = TwpConfigLoader.LoadYaml(f.ConfigText()!);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
        Assert.IsTrue(config.UserRoutes().Any(r => r.Id == "ingress/default/shop/r0p0"));
        var ingress = f.Api.Get("ingresses", "default", "shop")!;
        Assert.AreEqual("203.0.113.10", ingress["status"]!["loadBalancer"]!["ingress"]![0]!["ip"]!.GetValue<string>());
        Assert.AreEqual(0, f.Api.WritesTo("/events").Count);
    }

    [TestMethod]
    public async Task AnIngressInTheFakeClusterOnlyServes_WhenItsClassIsOurs()
    {
        await using var f = new ClusterFixture();
        SeedIngress(f);
        f.Api.Apply("ingressclasses", ClusterFixture.Obj("""{ "metadata": { "name": "titanium" }, "spec": { "controller": "example.com/other" } }"""));
        await f.StartCacheAsync();
        await SettleAsync(f, 1);

        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        var config = TwpConfigLoader.LoadYaml(f.ConfigText()!);
        Assert.IsFalse(config.UserRoutes().Any(r => r.Id.StartsWith("ingress/", StringComparison.Ordinal)));
        Assert.IsNull(f.Api.Get("ingresses", "default", "shop")!["status"]);
    }

    [TestMethod]
    public async Task ARejectedRule_PostsOneWarningEvent_ThatIsNotRepeatedByLaterReconciles()
    {
        await using var f = new ClusterFixture();
        SeedIngress(f, service: "missing");
        await f.StartCacheAsync();
        await SettleAsync(f, 1);

        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        var posts = f.Api.WritesTo("/namespaces/default/events").Where(w => w.Method == HttpMethod.Post).ToList();
        Assert.AreEqual(1, posts.Count);
        var body = JsonNode.Parse(posts[0].Body!)!;
        Assert.AreEqual("Warning", body["type"]!.GetValue<string>());
        Assert.AreEqual("BackendNotFound", body["reason"]!.GetValue<string>());
        Assert.AreEqual("Ingress", body["involvedObject"]!["kind"]!.GetValue<string>());
        Assert.AreEqual("shop", body["involvedObject"]!["name"]!.GetValue<string>());
        Assert.AreEqual(1, f.Api.List("events").Count);
    }

    [TestMethod]
    public async Task AnUnknownAnnotation_IsReportedAsAnEvent_AndTheIngressStillServes()
    {
        await using var f = new ClusterFixture();
        SeedIngress(f, annotations: "\"titanium.io/bogus\": \"1\"");
        await f.StartCacheAsync();
        await SettleAsync(f, 1);

        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.AreEqual("UnknownAnnotation", f.Api.List("events").Single()["reason"]!.GetValue<string>());
        Assert.IsTrue(TwpConfigLoader.LoadYaml(f.ConfigText()!).UserRoutes().Any(r => r.Id == "ingress/default/shop/r0p0"));
    }

    [TestMethod]
    public async Task FixingTheIngress_ClearsItsEventKey_SoARecurrenceIsPostedAgain()
    {
        await using var f = new ClusterFixture();
        SeedIngress(f, service: "missing");
        await f.StartCacheAsync();
        await SettleAsync(f, 1);
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        SeedIngress(f);
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().Ingresses.Single().Spec.Rules![0].Http!.Paths![0].Backend!.Service!.Name == "web");
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        SeedIngress(f, service: "missing");
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().Ingresses.Single().Spec.Rules![0].Http!.Paths![0].Backend!.Service!.Name == "missing");
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.AreEqual(2, f.Api.WritesTo("/namespaces/default/events").Count(w => w.Method == HttpMethod.Post), "posted again after the key had been cleared");
        Assert.AreEqual(1, f.Api.List("events").Count, "the same event object, so the API server's 409 is a success");
    }

    [TestMethod]
    public async Task ADeletedIngress_DisappearsFromTheConfig()
    {
        await using var f = new ClusterFixture();
        SeedIngress(f);
        await f.StartCacheAsync();
        await SettleAsync(f, 1);
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        f.Api.Delete("ingresses", "default", "shop");
        await SettleAsync(f, 0);
        var outcome = await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.IsTrue(outcome.WroteOutput);
        Assert.IsFalse(TwpConfigLoader.LoadYaml(f.ConfigText()!).UserRoutes().Any(r => r.Id.StartsWith("ingress/", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Reconcile_WritesNothingWhenNothingChanged_WithAnIngressPresent()
    {
        await using var f = new ClusterFixture();
        SeedIngress(f, service: "missing");
        await f.StartCacheAsync();
        await SettleAsync(f, 1);
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().Ingresses.Single().Status?.LoadBalancer?.Ingress?.Count > 0);
        var before = f.Api.Writes.Count;

        var outcome = await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.IsFalse(outcome.WroteOutput);
        Assert.AreEqual(0, outcome.StatusWritten);
        Assert.AreEqual(before, f.Api.Writes.Count);
    }

    // ---- EventWriter on its own ----

    private static IngressEvent Event(string reason = "BackendNotFound", string message = "boom", string name = "shop") =>
        new("default", name, "uid-" + name, "Warning", reason, message);

    [TestMethod]
    public async Task EventWriter_PostsEachEventOnce_AndBumpsItAfterTheRepeatInterval()
    {
        var api = new FakeKubeApi();
        var time = new ManualTime();
        var writer = new EventWriter(api, "titanium.io/gateway", NullLogger.Instance, time) { RepeatAfter = TimeSpan.FromMinutes(10) };
        var events = new[] { Event() };

        Assert.AreEqual(1, await writer.ApplyAsync(events, CancellationToken.None));
        Assert.AreEqual(0, await writer.ApplyAsync(events, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.AreEqual(1, await writer.ApplyAsync(events, CancellationToken.None));

        var stored = api.List("events").Single();
        Assert.AreEqual(2, stored["count"]!.GetValue<int>());
        Assert.AreEqual(1, api.WritesTo("/events").Count(w => w.Method == HttpMethod.Post));
        Assert.AreEqual(1, api.WritesTo("/events").Count(w => w.Method == HttpMethod.Patch));
    }

    [TestMethod]
    public async Task EventWriter_DifferentMessagesAreDifferentEvents()
    {
        var api = new FakeKubeApi();
        var writer = new EventWriter(api, "titanium.io/gateway", NullLogger.Instance, new ManualTime());

        await writer.ApplyAsync([Event(message: "a"), Event(message: "b"), Event(reason: "Other", message: "a")], CancellationToken.None);

        Assert.AreEqual(3, api.List("events").Count);
    }

    [TestMethod]
    public async Task EventWriter_AnEventThatReturnsAfterBeingResolved_IsPostedAgain()
    {
        var api = new FakeKubeApi();
        var writer = new EventWriter(api, "titanium.io/gateway", NullLogger.Instance, new ManualTime());

        await writer.ApplyAsync([Event()], CancellationToken.None);
        await writer.ApplyAsync([], CancellationToken.None);
        var sent = await writer.ApplyAsync([Event()], CancellationToken.None);

        Assert.AreEqual(1, sent);
    }

    [TestMethod]
    public async Task EventWriter_ASurvivingEventWithADeletedObject_IsPostedFresh()
    {
        var api = new FakeKubeApi();
        var time = new ManualTime();
        var writer = new EventWriter(api, "titanium.io/gateway", NullLogger.Instance, time) { RepeatAfter = TimeSpan.FromMinutes(1) };
        await writer.ApplyAsync([Event()], CancellationToken.None);
        var stored = api.List("events").Single()["metadata"]!;
        api.Delete("events", "default", stored["name"]!.GetValue<string>());

        time.Advance(TimeSpan.FromMinutes(2));
        await writer.ApplyAsync([Event()], CancellationToken.None);

        Assert.AreEqual(1, api.List("events").Count);
    }

    [TestMethod]
    public async Task EventWriter_AnAlreadyExistingEvent_CountsAsPosted()
    {
        var api = new FakeKubeApi();
        var first = new EventWriter(api, "titanium.io/gateway", NullLogger.Instance, new ManualTime());
        var second = new EventWriter(api, "titanium.io/gateway", NullLogger.Instance, new ManualTime());

        await first.ApplyAsync([Event()], CancellationToken.None);
        await second.ApplyAsync([Event()], CancellationToken.None);

        Assert.AreEqual(1, api.List("events").Count);
        Assert.AreEqual(0, await second.ApplyAsync([Event()], CancellationToken.None));
    }

    [TestMethod]
    public async Task EventWriter_AnApiFailure_DoesNotThrow_AndIsRetriedNextTime()
    {
        var api = new FakeKubeApi { Offline = true };
        var writer = new EventWriter(api, "titanium.io/gateway", NullLogger.Instance, new ManualTime());

        await writer.ApplyAsync([Event()], CancellationToken.None);
        api.Offline = false;
        var sent = await writer.ApplyAsync([Event()], CancellationToken.None);

        Assert.AreEqual(1, sent);
        Assert.AreEqual(1, api.List("events").Count);
    }
}
