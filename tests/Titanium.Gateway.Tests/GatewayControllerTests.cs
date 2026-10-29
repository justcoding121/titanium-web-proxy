using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Titanium.Gateway.Control;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Gateway.Tests;

/// <summary>The controller against an in-memory API server: informers, rendering, publishing, status.</summary>
[TestClass]
public sealed class GatewayControllerTests
{
    private static JsonObject Status(ClusterFixture f, string plural, string? ns, string name) =>
        f.Api.Get(plural, ns, name)!["status"]!.AsObject();

    private static string ConditionStatus(JsonNode? conditions, string type) =>
        conditions!.AsArray().First(c => c!["type"]!.GetValue<string>() == type)!["status"]!.GetValue<string>();

    [TestMethod]
    public async Task Reconcile_PublishesAValidConfigAndProgramsTheGateway()
    {
        await using var f = new ClusterFixture();
        await f.StartCacheAsync();

        var outcome = await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.IsTrue(outcome.Leader);
        Assert.IsTrue(outcome.WroteOutput);
        Assert.IsTrue(outcome.Confirmed);
        var text = f.ConfigText();
        Assert.IsNotNull(text);
        var config = TwpConfigLoader.LoadYaml(text);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
        Assert.AreEqual(1, config.UserRoutes().Count);
        Assert.AreEqual(GatewayTranslator.NoMatchRouteId, config.Routes[^1].Id, "a request no route matches gets 404, never a forward to the named host");
        Assert.AreEqual(404, config.Routes[^1].DirectResponse?.Status);
        CollectionAssert.AreEqual(new[] { "app.test" }, config.UserRoutes()[0].Match.Hosts!.ToArray());
        var destination = config.Clusters.Single().Destinations.Single();
        Assert.AreEqual("10.0.0.1", destination.Address);
        Assert.AreEqual(8080, destination.Port);
        Assert.AreEqual(8080, config.Listeners.Single().Port);

        var gateway = Status(f, "gateways", "default", "gw");
        Assert.AreEqual("True", ConditionStatus(gateway["conditions"], "Accepted"));
        Assert.AreEqual("True", ConditionStatus(gateway["conditions"], "Programmed"));
        Assert.AreEqual(1, gateway["listeners"]![0]!["attachedRoutes"]!.GetValue<int>());
        Assert.AreEqual("203.0.113.10", gateway["addresses"]![0]!["value"]!.GetValue<string>());
        Assert.AreEqual("True", ConditionStatus(Status(f, "gatewayclasses", null, "titanium")["conditions"], "Accepted"));

        var parent = Status(f, "httproutes", "default", "web")["parents"]![0]!;
        Assert.AreEqual(ClusterFixture.Controller, parent["controllerName"]!.GetValue<string>());
        Assert.AreEqual("True", ConditionStatus(parent["conditions"], "Accepted"));
        Assert.AreEqual("True", ConditionStatus(parent["conditions"], "ResolvedRefs"));
    }

    [TestMethod]
    public async Task Reconcile_WritesNothingWhenNothingChanged()
    {
        await using var f = new ClusterFixture();
        await f.StartCacheAsync();
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().Gateways.Single().Status?.Conditions?.Count > 0
                                                && f.Cache.Snapshot().HttpRoutes.Single().Status?.Parents?.Count > 0
                                                && f.Cache.Snapshot().GatewayClasses.Single().Status?.Conditions?.Count > 0);
        var before = f.Api.Writes.Count;

        var outcome = await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.IsFalse(outcome.WroteOutput);
        Assert.AreEqual(0, outcome.StatusWritten);
        Assert.AreEqual(before, f.Api.Writes.Count);
    }

    [TestMethod]
    public async Task Reconcile_PicksUpANewRouteFromTheWatch()
    {
        await using var f = new ClusterFixture();
        await f.StartCacheAsync();
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        f.AddRoute("api", "api.test");
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().HttpRoutes.Count == 2);
        var outcome = await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.IsTrue(outcome.WroteOutput);
        var config = TwpConfigLoader.LoadYaml(f.ConfigText()!);
        Assert.AreEqual(2, config.UserRoutes().Count);
        Assert.AreEqual(2, Status(f, "gateways", "default", "gw")["listeners"]![0]!["attachedRoutes"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Reconcile_AsksDataPlanePodsToRefreshTheirConfigVolume_AndRepeatsForAPodThatLags()
    {
        await using var f = new ClusterFixture();
        await f.StartCacheAsync();
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        string Token(string pod) => f.Api.WritesTo("/pods/" + pod)
            .Select(w => JsonNode.Parse(w.Body!)!["metadata"]!["annotations"]![OutputWriter.PokeAnnotation]!.GetValue<string>())
            .Last();
        var first = Token("data-0");
        Assert.AreEqual(16, first.Length, "the first poke carries the config hash");
        Assert.AreEqual(first, Token("data-1"));
        Assert.AreEqual("application/merge-patch+json", f.Api.WritesTo("/pods/data-0").First().ContentType);

        // Nothing changed and every pod runs the config: no further poke.
        var writes = f.Api.WritesTo("/pods/").Count;
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.AreEqual(writes, f.Api.WritesTo("/pods/").Count);

        // One pod still runs an old config: only that pod is poked again, with a new value so the pod really changes.
        f.Observer.Stale.Add("10.1.0.2");
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.AreEqual(first, Token("data-0"));
        Assert.AreEqual(first + ".1", Token("data-1"));
    }

    [TestMethod]
    public async Task Reconcile_ReactsWhenEndpointsChangeAndWhenTheyVanish()
    {
        await using var f = new ClusterFixture();
        await f.StartCacheAsync();
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        f.AddService("default", "web", 8080, "10.0.0.7", "10.0.0.8");
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().EndpointSlices.Any(s => s.Name == "web-abc" && s.Endpoints.Count == 2));
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        var addresses = TwpConfigLoader.LoadYaml(f.ConfigText()!).Clusters.Single().Destinations.Select(d => $"{d.Address}:{d.Port}").Order().ToArray();
        CollectionAssert.AreEqual(new[] { "10.0.0.7:8080", "10.0.0.8:8080" }, addresses);

        f.Api.Delete("endpointslices", "default", "web-abc");
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().EndpointSlices.All(s => s.Name != "web-abc"));
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        var config = TwpConfigLoader.LoadYaml(f.ConfigText()!);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
        Assert.IsNotNull(config.UserRoutes().Single().DirectResponse, "a route with no ready endpoint answers 500");
    }

    [TestMethod]
    public async Task Reconcile_ReportsNotProgrammedUntilEveryPodRunsTheNewConfig()
    {
        await using var f = new ClusterFixture();
        f.Observer.Stale.Add("10.1.0.2");
        await f.StartCacheAsync();

        var outcome = await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.IsFalse(outcome.Confirmed);
        var gateway = Status(f, "gateways", "default", "gw");
        Assert.AreEqual("True", ConditionStatus(gateway["conditions"], "Accepted"));
        Assert.AreEqual("False", ConditionStatus(gateway["conditions"], "Programmed"));

        f.Observer.Stale.Clear();
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().Gateways.Single().Status?.Conditions?.Count > 0);
        var again = await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.IsTrue(again.Confirmed);
        Assert.IsFalse(again.WroteOutput);
        Assert.AreEqual("True", ConditionStatus(Status(f, "gateways", "default", "gw")["conditions"], "Programmed"));
    }

    [TestMethod]
    public async Task Reconcile_KeepsRouteParentsOfOtherControllers()
    {
        await using var f = new ClusterFixture();
        var route = f.Api.Get("httproutes", "default", "web")!;
        route["status"] = ClusterFixture.Obj("""
            { "parents": [ { "parentRef": { "name": "other-gw" }, "controllerName": "example.com/other",
                             "conditions": [ { "type": "Accepted", "status": "True", "reason": "Accepted", "message": "", "observedGeneration": 1, "lastTransitionTime": "2026-01-01T00:00:00Z" } ] } ] }
            """);
        f.Api.Apply("httproutes", route);
        await f.StartCacheAsync();

        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        var parents = Status(f, "httproutes", "default", "web")["parents"]!.AsArray();
        Assert.AreEqual(2, parents.Count);
        Assert.IsTrue(parents.Any(p => p!["controllerName"]!.GetValue<string>() == "example.com/other"));
        Assert.IsTrue(parents.Any(p => p!["controllerName"]!.GetValue<string>() == ClusterFixture.Controller));
    }

    [TestMethod]
    public async Task Reconcile_KeepsLastTransitionTimeWhileAConditionHoldsItsStatus()
    {
        await using var f = new ClusterFixture();
        await f.StartCacheAsync();
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        var first = Status(f, "gateways", "default", "gw")["conditions"]!.AsArray()
            .First(c => c!["type"]!.GetValue<string>() == "Accepted")!["lastTransitionTime"]!.GetValue<string>();

        f.AddRoute("api", "api.test");
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().HttpRoutes.Count == 2);
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        var later = Status(f, "gateways", "default", "gw")["conditions"]!.AsArray()
            .First(c => c!["type"]!.GetValue<string>() == "Accepted")!["lastTransitionTime"]!.GetValue<string>();
        Assert.AreEqual(first, later);
    }

    [TestMethod]
    public async Task Reconcile_ConvertsATlsSecretToAPfxAndRejectsABrokenOne()
    {
        await using var f = new ClusterFixture();
        f.Api.Apply("secrets", ClusterFixture.TlsSecret("default", "good", valid: true));
        f.Api.Apply("secrets", ClusterFixture.TlsSecret("default", "bad", valid: false));
        var gateway = f.Api.Get("gateways", "default", "gw")!;
        gateway["spec"]!["listeners"] = ClusterFixture.Obj("""
            { "l": [
              { "name": "http", "port": 80, "protocol": "HTTP", "allowedRoutes": { "namespaces": { "from": "All" } } },
              { "name": "good", "port": 443, "protocol": "HTTPS", "hostname": "app.test", "tls": { "certificateRefs": [ { "name": "good" } ] }, "allowedRoutes": { "namespaces": { "from": "All" } } },
              { "name": "bad", "port": 443, "protocol": "HTTPS", "hostname": "bad.test", "tls": { "certificateRefs": [ { "name": "bad" } ] } }
            ] }
            """)["l"]!.DeepClone();
        f.Api.Apply("gateways", gateway);
        await f.StartCacheAsync();

        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        var secret = f.Api.Get("secrets", ClusterFixture.DataNamespace, ClusterFixture.DataService + "-tls")!;
        var files = secret["data"]!.AsObject().Select(p => p.Key).ToArray();
        CollectionAssert.AreEqual(new[] { "default.good.pfx" }, files);
        var pfx = Convert.FromBase64String(secret["data"]!["default.good.pfx"]!.GetValue<string>());
        using var loaded = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(pfx, null);
        Assert.IsTrue(loaded.HasPrivateKey);

        var config = TwpConfigLoader.LoadYaml(f.ConfigText()!);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
        Assert.AreEqual("/etc/titanium-tls/default.good.pfx", config.Certificates!.Items.Single().Path);

        var listeners = Status(f, "gateways", "default", "gw")["listeners"]!.AsArray();
        var badStatus = listeners.First(l => l!["name"]!.GetValue<string>() == "bad")!["conditions"];
        Assert.AreEqual("False", ConditionStatus(badStatus, "ResolvedRefs"));
        var goodStatus = listeners.First(l => l!["name"]!.GetValue<string>() == "good")!["conditions"];
        Assert.AreEqual("True", ConditionStatus(goodStatus, "ResolvedRefs"));
    }

    private static string? ListenersAnnotation(ClusterFixture f) =>
        f.Api.Get("deployments", ClusterFixture.DataNamespace, ClusterFixture.DataService)!["spec"]!["template"]!["metadata"]!["annotations"]?[OutputWriter.ListenersAnnotation]?.GetValue<string>();

    [TestMethod]
    public async Task Reconcile_RollsTheDataPlaneOnlyWhenTheListenerSetChanges()
    {
        await using var f = new ClusterFixture();
        await f.StartCacheAsync();

        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        var first = ListenersAnnotation(f);
        Assert.IsNotNull(first, "the first listener starts a rollout");
        Assert.AreNotEqual(OutputWriter.NoListeners, first);
        var patches = f.Api.WritesTo("/deployments/").Count;

        // A new route changes routes and clusters, which the pods reload in place.
        f.AddRoute("api", "api.test");
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().HttpRoutes.Count == 2);
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.AreEqual(patches, f.Api.WritesTo("/deployments/").Count);
        Assert.AreEqual(first, ListenersAnnotation(f));

        // A TLS listener changes what the pods must bind, which they only read at start.
        f.Api.Apply("secrets", ClusterFixture.TlsSecret("default", "good", valid: true));
        var gateway = f.Api.Get("gateways", "default", "gw")!;
        gateway["spec"]!["listeners"]!.AsArray().Add(ClusterFixture.Obj("""
            { "name": "https", "port": 443, "protocol": "HTTPS", "hostname": "app.test", "tls": { "certificateRefs": [ { "name": "good" } ] }, "allowedRoutes": { "namespaces": { "from": "All" } } }
            """));
        f.Api.Apply("gateways", gateway);
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().Gateways.Single().Spec.Listeners.Count == 2);
        await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.AreNotEqual(first, ListenersAnnotation(f));
    }

    [TestMethod]
    public async Task Follower_WritesNothing()
    {
        await using var f = new ClusterFixture();
        f.Api.Apply("leases", ClusterFixture.Obj("""
            { "metadata": { "name": "titanium-gateway", "namespace": "titanium-system" },
              "spec": { "holderIdentity": "someone-else", "leaseDurationSeconds": 15, "renewTime": "2026-10-28T10:00:00.000000Z" } }
            """));
        var elector = new LeaderElector(f.Api, new LeaderElectionOptions { Namespace = "titanium-system", Identity = "me" }, NullLogger.Instance);
        await elector.StepAsync(CancellationToken.None);
        await f.StartCacheAsync(elector);

        var outcome = await f.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.IsFalse(elector.IsLeader);
        Assert.IsFalse(outcome.Leader);
        Assert.AreEqual(0, f.Api.Writes.Count);
        Assert.IsNull(f.ConfigText());
    }

    [TestMethod]
    public async Task Loop_CollapsesABurstOfChangesIntoFewPasses()
    {
        await using var f = new ClusterFixture();
        await f.StartLoopAsync(minInterval: TimeSpan.FromMilliseconds(300));
        await ClusterFixture.WaitForAsync(() => f.ConfigText() is not null);
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().Gateways.Single().Status?.Conditions?.Count > 0);
        await Task.Delay(400);
        var configWrites = f.Api.WritesTo("/configmaps/").Count;

        for (var i = 0; i < 30; i++)
        {
            f.AddRoute("burst" + i, $"b{i}.test");
        }

        await ClusterFixture.WaitForAsync(() => f.ConfigText() is { } text && TwpConfigLoader.LoadYaml(text).Routes.Count == 32, 15000);
        await Task.Delay(800);

        var writes = f.Api.WritesTo("/configmaps/").Count - configWrites;
        Assert.IsTrue(writes is >= 1 and <= 6, $"30 changes within a moment caused {writes} ConfigMap writes");
    }

    [TestMethod]
    public async Task Informer_RelistsAfterTheWatchExpires()
    {
        await using var f = new ClusterFixture();
        f.Api.ExpireNextWatch = true;
        await f.StartCacheAsync();
        const int initialLists = 8; // one per collection, Secrets included

        await ClusterFixture.WaitForAsync(() => f.Api.ListCount > initialLists, 15000);

        f.AddRoute("late", "late.test");
        await ClusterFixture.WaitForAsync(() => f.Cache.Snapshot().HttpRoutes.Count == 2);
    }
}
