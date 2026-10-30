using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Discovery;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

[TestClass]
public class ServiceDiscoveryHardeningTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private static readonly string[] K8sAddress10003 = ["10.0.0.3"];
    private static readonly string[] K8sAddress10001 = ["10.0.0.1"];
    private static readonly string[] K8sAddresses10001And10003 = ["10.0.0.1", "10.0.0.3"];
    private static readonly string[] ConsulAddress100101 = ["10.1.0.1"];
    private static readonly int[] ConsulPollSequence = [1, 2, 3];

    // ---------------------------------------------------------------- Kubernetes

    [TestMethod]
    public async Task Kubernetes_ListThenWatch_AppliesEventsBookmarksAndDeletes()
    {
        var release = NewGate();
        await using var api = await FakeApi.StartAsync(async ctx =>
        {
            if (!IsWatch(ctx))
            {
                await WriteJsonAsync(ctx, SliceList("100",
                    Slice("web-a", "10", [("10.0.0.1", true), ("10.0.0.2", false)], 8080)));
                return;
            }

            // First watch window only. A reconnect after the stream ends just waits.
            if (ctx.Request.Query["resourceVersion"] == "100")
            {
                await StreamLineAsync(ctx, Event("ADDED", Slice("web-b", "101", [("10.0.1.1", true)], 8080)));
                await StreamLineAsync(ctx, Event("BOOKMARK", Bookmark("105")));
                await StreamLineAsync(ctx, Event("MODIFIED", Slice("web-a", "106", [("10.0.0.3", true)], 8080)));
                await StreamLineAsync(ctx, Event("DELETED", Slice("web-b", "107", [("10.0.1.1", true)], 8080)));
                await release.Task;
                return;
            }

            await HoldOpenAsync(ctx);
        });

        var manager = new ClusterManager();
        using var discovery = StartK8s(manager, api, "svc");

        await WaitUntilAsync(() => Addresses(manager, "k8s").SequenceEqual(["10.0.0.3"]));
        CollectionAssert.AreEqual(K8sAddress10003, Addresses(manager, "k8s").ToArray(),
            "A not-ready endpoint is skipped, the MODIFIED slice replaces the original, DELETED removes slice b.");

        var list = api.Requests.First(r => !r.IsWatch);
        StringAssert.Contains(list.Path, "/apis/discovery.k8s.io/v1/namespaces/prod/endpointslices");
        StringAssert.Contains(list.Query, "labelSelector=kubernetes.io%2Fservice-name%3Dsvc");

        var watch = api.Requests.First(r => r.IsWatch);
        StringAssert.Contains(watch.Query, "resourceVersion=100");
        StringAssert.Contains(watch.Query, "allowWatchBookmarks=true");
        StringAssert.Contains(watch.Query, "watch=true");

        release.TrySetResult();
        await WaitUntilAsync(() => api.Requests.Count(r => r.IsWatch) >= 2);
        Assert.AreEqual("107", ResourceVersionOf(api.Requests.Where(r => r.IsWatch).ElementAt(1)),
            "The reconnect resumes from the last event's resourceVersion.");
    }

    [TestMethod]
    public async Task Kubernetes_BookmarkOnly_AdvancesResourceVersionWithoutApplying()
    {
        var release = NewGate();
        await using var api = await FakeApi.StartAsync(async ctx =>
        {
            if (!IsWatch(ctx))
            {
                await WriteJsonAsync(ctx, SliceList("5", Slice("web-a", "5", [("10.0.0.1", true)], 80)));
                return;
            }

            if (ctx.Request.Query["resourceVersion"] == "5")
            {
                await StreamLineAsync(ctx, Event("BOOKMARK", Bookmark("77")));
                await release.Task;
                return;
            }

            await HoldOpenAsync(ctx);
        });

        var manager = new ClusterManager();
        using var discovery = StartK8s(manager, api, "svc");
        await WaitUntilAsync(() => Addresses(manager, "k8s").Count > 0);
        release.TrySetResult();
        await WaitUntilAsync(() => api.Requests.Count(r => r.IsWatch) >= 2);
        Assert.AreEqual("77", ResourceVersionOf(api.Requests.Where(r => r.IsWatch).ElementAt(1)));
        CollectionAssert.AreEqual(K8sAddress10001, Addresses(manager, "k8s").ToArray());
    }

    [TestMethod]
    public async Task Kubernetes_HttpGoneOnWatch_Relists()
    {
        var lists = 0;
        await using var api = await FakeApi.StartAsync(async ctx =>
        {
            if (!IsWatch(ctx))
            {
                var n = Interlocked.Increment(ref lists);
                await WriteJsonAsync(ctx, n == 1
                    ? SliceList("100", Slice("web-a", "100", [("10.0.0.1", true)], 80))
                    : SliceList("200", Slice("web-a", "200", [("10.0.0.9", true)], 80)));
                return;
            }

            if (ctx.Request.Query["resourceVersion"] == "100")
            {
                ctx.Response.StatusCode = StatusCodes.Status410Gone;
                await ctx.Response.WriteAsync("""{"kind":"Status","code":410,"reason":"Expired"}""");
                return;
            }

            await HoldOpenAsync(ctx);
        });

        var manager = new ClusterManager();
        using var discovery = StartK8s(manager, api, "svc");

        await WaitUntilAsync(() => Addresses(manager, "k8s").SequenceEqual(["10.0.0.9"]));
        Assert.AreEqual(2, Volatile.Read(ref lists), "A 410 on the watch drops the resourceVersion and relists.");
        await WaitUntilAsync(() => api.Requests.Any(r => r.IsWatch && ResourceVersionOf(r) == "200"));
    }

    [TestMethod]
    public async Task Kubernetes_ErrorEventGone_Relists()
    {
        var lists = 0;
        await using var api = await FakeApi.StartAsync(async ctx =>
        {
            if (!IsWatch(ctx))
            {
                var n = Interlocked.Increment(ref lists);
                await WriteJsonAsync(ctx, n == 1
                    ? SliceList("1", Slice("web-a", "1", [("10.0.0.1", true)], 80))
                    : SliceList("50", Slice("web-a", "50", [("10.0.0.2", true)], 80)));
                return;
            }

            if (ctx.Request.Query["resourceVersion"] == "1")
            {
                await StreamLineAsync(ctx,
                    """{"type":"ERROR","object":{"kind":"Status","apiVersion":"v1","code":410,"message":"too old resource version"}}""");
                return;
            }

            await HoldOpenAsync(ctx);
        });

        var manager = new ClusterManager();
        using var discovery = StartK8s(manager, api, "svc");
        await WaitUntilAsync(() => Addresses(manager, "k8s").SequenceEqual(["10.0.0.2"]));
        Assert.AreEqual(2, Volatile.Read(ref lists));
    }

    [TestMethod]
    public async Task Kubernetes_KeepsLastKnownGood_OnErrorsAndEmptyResults()
    {
        var phase = 0; // 0 healthy, 1 failing, 2 empty relist, 3 recovered
        var lists = 0;
        await using var api = await FakeApi.StartAsync(async ctx =>
        {
            if (!IsWatch(ctx))
            {
                Interlocked.Increment(ref lists);
                switch (Volatile.Read(ref phase))
                {
                    case 0:
                        await WriteJsonAsync(ctx, SliceList("1", Slice("web-a", "1", [("10.0.0.1", true)], 80)));
                        return;
                    case 1:
                        ctx.Response.StatusCode = 503;
                        return;
                    case 2:
                        await WriteJsonAsync(ctx, SliceList("2"));
                        return;
                    default:
                        await WriteJsonAsync(ctx, SliceList("3", Slice("web-a", "3", [("10.0.0.7", true)], 80)));
                        return;
                }
            }

            if (Volatile.Read(ref phase) <= 2)
            {
                // Expire the resourceVersion so every step has to relist.
                ctx.Response.StatusCode = StatusCodes.Status410Gone;
                return;
            }

            await HoldOpenAsync(ctx);
        });

        var manager = new ClusterManager();
        using var discovery = StartK8s(manager, api, "svc");
        await WaitUntilAsync(() => Addresses(manager, "k8s").SequenceEqual(["10.0.0.1"]));

        Volatile.Write(ref phase, 1);
        await WaitUntilAsync(() => Volatile.Read(ref lists) >= 4, "relist should keep retrying with backoff");
        CollectionAssert.AreEqual(K8sAddress10001, Addresses(manager, "k8s").ToArray(),
            "HTTP 503 must not replace or clear the last known destinations.");

        Volatile.Write(ref phase, 2);
        var seen = Volatile.Read(ref lists);
        await WaitUntilAsync(() => Volatile.Read(ref lists) >= seen + 2);
        CollectionAssert.AreEqual(K8sAddress10001, Addresses(manager, "k8s").ToArray(),
            "An empty result is never applied.");

        Volatile.Write(ref phase, 3);
        await WaitUntilAsync(() => Addresses(manager, "k8s").SequenceEqual(["10.0.0.7"]));
    }

    [TestMethod]
    public async Task Kubernetes_ConnectionFailure_KeepsDestinationsAndRecovers()
    {
        var failWatch = 0;
        var lists = 0;
        await using var api = await FakeApi.StartAsync(async ctx =>
        {
            if (!IsWatch(ctx))
            {
                Interlocked.Increment(ref lists);
                await WriteJsonAsync(ctx, SliceList("1", Slice("web-a", "1", [("10.0.0.1", true)], 80)));
                return;
            }

            if (Interlocked.Increment(ref failWatch) <= 3)
            {
                ctx.Abort(); // connection reset mid-request
                return;
            }

            await StreamLineAsync(ctx, Event("MODIFIED", Slice("web-a", "2", [("10.0.0.2", true)], 80)));
            await HoldOpenAsync(ctx);
        });

        var manager = new ClusterManager();
        using var discovery = StartK8s(manager, api, "svc");
        await WaitUntilAsync(() => Addresses(manager, "k8s").SequenceEqual(["10.0.0.1"]));
        await WaitUntilAsync(() => Addresses(manager, "k8s").SequenceEqual(["10.0.0.2"]));
        Assert.AreEqual(1, Volatile.Read(ref lists), "Plain watch failures resume from the resourceVersion without relisting.");
        Assert.IsTrue(Volatile.Read(ref failWatch) >= 4);
    }

    [TestMethod]
    public async Task Kubernetes_ServiceAccountToken_IsRereadOnRotation()
    {
        var sa = NewServiceAccountDir("token-one");
        var release = NewGate();
        var watches = 0;
        try
        {
            await using var api = await FakeApi.StartAsync(async ctx =>
            {
                if (!IsWatch(ctx))
                {
                    await WriteJsonAsync(ctx, SliceList("1", Slice("web-a", "1", [("10.0.0.1", true)], 80)));
                    return;
                }

                if (Interlocked.Increment(ref watches) == 1)
                {
                    await StreamLineAsync(ctx, Event("BOOKMARK", Bookmark("2")));
                    await release.Task;
                    return;
                }

                await HoldOpenAsync(ctx);
            });

            var manager = new ClusterManager();
            using var discovery = StartK8s(manager, api, "svc", new Dictionary<string, string>
            {
                ["discovery.k8sServiceAccountDir"] = sa,
                ["discovery.k8sTokenRefreshSeconds"] = "0",
            });

            await WaitUntilAsync(() => api.Requests.Any(r => r.IsWatch));
            await File.WriteAllTextAsync(Path.Combine(sa, "token"), "token-two\n");
            release.TrySetResult();
            await WaitUntilAsync(() => api.Requests.Count(r => r.IsWatch) >= 2);

            Assert.AreEqual("Bearer token-one", api.Requests.First(r => !r.IsWatch).Authorization);
            Assert.AreEqual("Bearer token-one", api.Requests.First(r => r.IsWatch).Authorization);
            Assert.AreEqual("Bearer token-two", api.Requests.Where(r => r.IsWatch).ElementAt(1).Authorization,
                "The rotated projected token must be used on the next watch connection.");
        }
        finally
        {
            DeleteQuietly(sa);
        }
    }

    [TestMethod]
    public async Task Kubernetes_Unauthorized_ReloadsTokenWithoutWaitingForTtl()
    {
        var sa = NewServiceAccountDir("stale");
        try
        {
            await using var api = await FakeApi.StartAsync(async ctx =>
            {
                if (ctx.Request.Headers.Authorization != "Bearer fresh")
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                if (!IsWatch(ctx))
                {
                    await WriteJsonAsync(ctx, SliceList("1", Slice("web-a", "1", [("10.0.0.1", true)], 80)));
                    return;
                }

                await HoldOpenAsync(ctx);
            });

            var manager = new ClusterManager();
            using var discovery = StartK8s(manager, api, "svc", new Dictionary<string, string>
            {
                ["discovery.k8sServiceAccountDir"] = sa,
                ["discovery.k8sTokenRefreshSeconds"] = "3600",
            });

            await WaitUntilAsync(() => api.Requests.Length >= 1);
            await File.WriteAllTextAsync(Path.Combine(sa, "token"), "fresh");
            await WaitUntilAsync(() => Addresses(manager, "k8s").SequenceEqual(["10.0.0.1"]));
        }
        finally
        {
            DeleteQuietly(sa);
        }
    }

    [TestMethod]
    public async Task Kubernetes_ExplicitSliceUrl_WatchesThatCollection()
    {
        await using var api = await FakeApi.StartAsync(async ctx =>
        {
            if (!IsWatch(ctx))
            {
                await WriteJsonAsync(ctx, SliceList("9", Slice("web-a", "9", [("10.0.0.5", true)], 8443, "https")));
                return;
            }

            await HoldOpenAsync(ctx);
        });

        var manager = new ClusterManager();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["discovery.mode"] = "k8s",
            ["discovery.k8sUrl"] = api.BaseUrl + "/apis/discovery.k8s.io/v1/namespaces/x/endpointslices?labelSelector=app%3Dweb",
            ["discovery.k8sPortName"] = "https",
            ["discovery.clusterId"] = "k8s",
        };
        using var discovery = ServiceDiscovery.TryStart(Context(manager, options), options);
        await WaitUntilAsync(() => Addresses(manager, "k8s").SequenceEqual(["10.0.0.5"]));
        Assert.AreEqual(8443, manager.Snapshot.Clusters["k8s"].Destinations[0].Port);
        Assert.AreEqual("/apis/discovery.k8s.io/v1/namespaces/x/endpointslices", api.Requests[0].Path);
        StringAssert.Contains(api.Requests[0].Query, "labelSelector=app%3Dweb");
    }

    [TestMethod]
    public async Task Kubernetes_SingleEndpointsObject_StillPolled()
    {
        var calls = 0;
        await using var api = await FakeApi.StartAsync(async ctx =>
        {
            var n = Interlocked.Increment(ref calls);
            await WriteJsonAsync(ctx, n == 1
                ? """{"subsets":[{"addresses":[{"ip":"10.0.0.5"}],"ports":[{"port":9000}]}]}"""
                : """{"subsets":[{"addresses":[{"ip":"10.0.0.6"}],"ports":[{"port":9000}]}]}""");
        });

        var manager = new ClusterManager();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["discovery.mode"] = "k8s",
            ["discovery.k8sUrl"] = api.BaseUrl + "/api/v1/namespaces/x/endpoints/web",
            ["discovery.intervalMs"] = "100",
            ["discovery.clusterId"] = "k8s",
        };
        using var discovery = ServiceDiscovery.TryStart(Context(manager, options), options);
        await WaitUntilAsync(() => Addresses(manager, "k8s").SequenceEqual(["10.0.0.6"]));
    }

    [TestMethod]
    public void Kubernetes_ParseEndpointSlice_FiltersNotReadyAndSelectsNamedPort()
    {
        using var doc = JsonDocument.Parse("""
            {"metadata":{"name":"s","resourceVersion":"1"},
             "ports":[{"name":"metrics","port":9090,"protocol":"TCP"},{"name":"http","port":8080,"protocol":"TCP"},{"name":"dns","port":53,"protocol":"UDP"}],
             "endpoints":[
               {"addresses":["10.0.0.1"],"conditions":{"ready":true}},
               {"addresses":["10.0.0.2"],"conditions":{"ready":false}},
               {"addresses":["10.0.0.3"]}]}
            """);
        var first = ServiceDiscovery.ParseEndpointSlice(doc.RootElement, null);
        CollectionAssert.AreEqual(K8sAddresses10001And10003, first.Select(d => d.Address).ToArray());
        Assert.IsTrue(first.All(d => d.Port == 9090));

        var named = ServiceDiscovery.ParseEndpointSlice(doc.RootElement, "http");
        Assert.IsTrue(named.All(d => d.Port == 8080));
        Assert.AreEqual(0, ServiceDiscovery.ParseEndpointSlice(doc.RootElement, "missing").Count);
        Assert.AreEqual(0, ServiceDiscovery.ParseEndpointSlice(doc.RootElement, "dns").Count, "UDP ports are ignored.");
    }

    // ---------------------------------------------------------------- Consul

    [TestMethod]
    public async Task Consul_BlockingQuery_UsesIndexWaitAndPassingFilter()
    {
        var release = NewGate();
        await using var api = await FakeApi.StartAsync(async ctx =>
        {
            if (ctx.Request.Query["index"] == "12")
            {
                await HoldOpenAsync(ctx);
                return;
            }

            if (ctx.Request.Query["index"] == "7")
            {
                await release.Task;
                ctx.Response.Headers["X-Consul-Index"] = "12";
                await WriteJsonAsync(ctx, ConsulBody(("web-1", "10.1.0.1", "passing"), ("web-3", "10.1.0.3", "passing")));
                return;
            }

            ctx.Response.Headers["X-Consul-Index"] = "7";
            await WriteJsonAsync(ctx, ConsulBody(
                ("web-1", "10.1.0.1", "passing"),
                ("web-2", "10.1.0.2", "critical"),
                ("web-4", "10.1.0.4", "warning")));
        });

        var manager = new ClusterManager();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["discovery.mode"] = "consul",
            ["discovery.consulUrl"] = api.BaseUrl + "/v1/health/service/web",
            ["discovery.consulWaitSeconds"] = "3",
            ["discovery.consulToken"] = "acl-token",
            ["discovery.clusterId"] = "consul",
        };
        using var discovery = ServiceDiscovery.TryStart(Context(manager, options), options);

        await WaitUntilAsync(() => Addresses(manager, "consul").SequenceEqual(["10.1.0.1"]));
        await WaitUntilAsync(() => api.Requests.Any(r => r.Query.Contains("index=7", StringComparison.Ordinal)));

        var blocking = api.Requests.First(r => r.Query.Contains("index=7", StringComparison.Ordinal));
        StringAssert.Contains(blocking.Query, "wait=3s");
        StringAssert.Contains(blocking.Query, "passing=true");
        Assert.AreEqual("acl-token", blocking.ConsulToken);
        Assert.IsFalse(api.Requests[0].Query.Contains("index=", StringComparison.Ordinal),
            "The first request is a plain read.");

        release.TrySetResult();
        await WaitUntilAsync(() => Addresses(manager, "consul").SequenceEqual(["10.1.0.1", "10.1.0.3"]));
        await WaitUntilAsync(() => api.Requests.Any(r => r.Query.Contains("index=12", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Consul_Errors_KeepLastKnownGood_AndEmptyIsNeverApplied()
    {
        var phase = 0; // 0 ok, 1 http 500, 2 garbage body, 3 empty array, 4 recovered
        var calls = 0;
        await using var api = await FakeApi.StartAsync(async ctx =>
        {
            Interlocked.Increment(ref calls);
            switch (Volatile.Read(ref phase))
            {
                case 0:
                    ctx.Response.Headers["X-Consul-Index"] = "3";
                    await WriteJsonAsync(ctx, ConsulBody(("web-1", "10.1.0.1", "passing")));
                    return;
                case 1:
                    ctx.Response.StatusCode = 500;
                    return;
                case 2:
                    await WriteJsonAsync(ctx, "<html>not json");
                    return;
                case 3:
                    ctx.Response.Headers["X-Consul-Index"] = "9";
                    await WriteJsonAsync(ctx, "[]");
                    return;
                default:
                    ctx.Response.Headers["X-Consul-Index"] = "10";
                    await WriteJsonAsync(ctx, ConsulBody(("web-9", "10.1.0.9", "passing")));
                    return;
            }
        });

        var manager = new ClusterManager();
        var options = ConsulOptions(api);
        options["discovery.intervalMs"] = "100";
        using var discovery = ServiceDiscovery.TryStart(Context(manager, options), options);
        await WaitUntilAsync(() => Addresses(manager, "consul").SequenceEqual(["10.1.0.1"]));

        foreach (var next in ConsulPollSequence)
        {
            Volatile.Write(ref phase, next);
            var seen = Volatile.Read(ref calls);
            await WaitUntilAsync(() => Volatile.Read(ref calls) >= seen + 3, $"phase {next} should keep retrying");
            CollectionAssert.AreEqual(ConsulAddress100101, Addresses(manager, "consul").ToArray(),
                $"phase {next} must retain the last known good destinations");
        }

        Volatile.Write(ref phase, 4);
        await WaitUntilAsync(() => Addresses(manager, "consul").SequenceEqual(["10.1.0.9"]));
    }

    [TestMethod]
    public async Task Consul_IndexGoingBackwards_ResetsToPlainRead()
    {
        var calls = 0;
        await using var api = await FakeApi.StartAsync(async ctx =>
        {
            var n = Interlocked.Increment(ref calls);
            ctx.Response.Headers["X-Consul-Index"] = n == 1 ? "50" : "4";
            await WriteJsonAsync(ctx, ConsulBody(("web-1", "10.1.0.1", "passing")));
            if (n >= 3)
            {
                await HoldOpenAsync(ctx);
            }
        });

        var manager = new ClusterManager();
        var options = ConsulOptions(api);
        options["discovery.intervalMs"] = "100";
        using var discovery = ServiceDiscovery.TryStart(Context(manager, options), options);
        await WaitUntilAsync(() => api.Requests.Length >= 3);

        Assert.IsTrue(api.Requests[1].Query.Contains("index=50", StringComparison.Ordinal));
        Assert.IsFalse(api.Requests[2].Query.Contains("index=50", StringComparison.Ordinal),
            "A lower index than before means the server state reset; the next call must not reuse the old index.");
    }

    [TestMethod]
    public void Consul_Parse_PassingOnlyAndNodeAddressFallback()
    {
        var json = """
            [{"Node":{"Address":"192.168.0.10"},"Service":{"ID":"a","Address":"","Port":8000},"Checks":[{"Status":"passing"},{"Status":"passing"}]},
             {"Node":{"Address":"192.168.0.11"},"Service":{"ID":"b","Address":"10.2.0.2","Port":8000},"Checks":[{"Status":"passing"},{"Status":"critical"}]}]
            """;
        var all = ServiceDiscovery.ParseConsulDestinations(json);
        Assert.AreEqual(2, all.Count);
        Assert.AreEqual("192.168.0.10", all[0].Address);

        var passing = ServiceDiscovery.ParseConsulDestinations(json, passingOnly: true);
        Assert.AreEqual(1, passing.Count);
        Assert.AreEqual("a", passing[0].Id);
    }

    // ---------------------------------------------------------------- Backoff

    [TestMethod]
    public void Backoff_GrowsExponentiallyWithJitterAndCaps()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), DiscoveryBackoff.Compute(0, 1000, 30_000, 0));
        Assert.AreEqual(TimeSpan.FromMilliseconds(1000), DiscoveryBackoff.Compute(0, 1000, 30_000, 1));
        Assert.AreEqual(TimeSpan.FromMilliseconds(2000), DiscoveryBackoff.Compute(1, 1000, 30_000, 1));
        Assert.AreEqual(TimeSpan.FromMilliseconds(4000), DiscoveryBackoff.Compute(2, 1000, 30_000, 1));
        Assert.AreEqual(TimeSpan.FromMilliseconds(30_000), DiscoveryBackoff.Compute(20, 1000, 30_000, 1));
        Assert.AreEqual(TimeSpan.FromMilliseconds(15_000), DiscoveryBackoff.Compute(99, 1000, 30_000, 0));

        var backoff = new DiscoveryBackoff(100, 400, () => 1.0);
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), backoff.Next());
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), backoff.Next());
        Assert.AreEqual(TimeSpan.FromMilliseconds(400), backoff.Next());
        Assert.AreEqual(TimeSpan.FromMilliseconds(400), backoff.Next());
        backoff.Reset();
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), backoff.Next());

        var jittered = new DiscoveryBackoff(100, 400);
        for (var i = 0; i < 50; i++)
        {
            var delay = jittered.Next();
            Assert.IsTrue(delay >= TimeSpan.FromMilliseconds(50) && delay <= TimeSpan.FromMilliseconds(400));
        }
    }

    // ---------------------------------------------------------------- Helpers

    private static ServiceDiscovery? StartK8s(
        ClusterManager manager, FakeApi api, string service, Dictionary<string, string>? extra = null)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["discovery.mode"] = "k8s",
            ["discovery.k8sUrl"] = api.BaseUrl,
            ["discovery.k8sService"] = service,
            ["discovery.k8sNamespace"] = "prod",
            ["discovery.clusterId"] = "k8s",
            ["discovery.backoffMinMs"] = "10",
            ["discovery.backoffMaxMs"] = "40",
        };
        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                options[pair.Key] = pair.Value;
            }
        }

        return ServiceDiscovery.TryStart(Context(manager, options), options);
    }

    private static Dictionary<string, string> ConsulOptions(FakeApi api) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["discovery.mode"] = "consul",
            ["discovery.consulUrl"] = api.BaseUrl + "/v1/health/service/web",
            ["discovery.consulWaitSeconds"] = "2",
            ["discovery.clusterId"] = "consul",
            ["discovery.backoffMinMs"] = "10",
            ["discovery.backoffMaxMs"] = "40",
        };

    private static PlusActivationContext Context(ClusterManager manager, Dictionary<string, string> options) =>
        new()
        {
            ProxyServer = new object(),
            ClusterManager = manager,
            Options = options,
        };

    private static List<string> Addresses(ClusterManager manager, string clusterId) =>
        manager.Snapshot.Clusters.TryGetValue(clusterId, out var cluster)
            ? cluster.Destinations.Select(d => d.Address).ToList()
            : [];

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsWatch(HttpContext ctx) => ctx.Request.Query["watch"] == "true";

    private static string? ResourceVersionOf(FakeRequest request)
    {
        foreach (var pair in request.Query.TrimStart('?').Split('&'))
        {
            if (pair.StartsWith("resourceVersion=", StringComparison.Ordinal))
            {
                return pair["resourceVersion=".Length..];
            }
        }

        return null;
    }

    private static string Slice(string name, string rv, (string Ip, bool Ready)[] endpoints, int port, string portName = "http")
    {
        var eps = string.Join(',', endpoints.Select(e =>
            $$$"""{"addresses":["{{{e.Ip}}}"],"conditions":{"ready":{{{(e.Ready ? "true" : "false")}}}}}"""));
        return $$"""
            {"apiVersion":"discovery.k8s.io/v1","kind":"EndpointSlice","metadata":{"name":"{{name}}","resourceVersion":"{{rv}}"},"addressType":"IPv4","ports":[{"name":"{{portName}}","port":{{port}},"protocol":"TCP"}],"endpoints":[{{eps}}]}
            """;
    }

    private static string SliceList(string rv, params string[] slices) =>
        $$"""{"apiVersion":"discovery.k8s.io/v1","kind":"EndpointSliceList","metadata":{"resourceVersion":"{{rv}}"},"items":[{{string.Join(',', slices)}}]}""";

    private static string Bookmark(string rv) =>
        $$$"""{"kind":"EndpointSlice","apiVersion":"discovery.k8s.io/v1","metadata":{"resourceVersion":"{{{rv}}}"}}""";

    private static string Event(string type, string obj) => $$"""{"type":"{{type}}","object":{{obj}}}""";

    private static string ConsulBody(params (string Id, string Address, string CheckStatus)[] services) =>
        "[" + string.Join(',', services.Select(s =>
            $$"""{"Node":{"Address":"192.168.9.9"},"Service":{"ID":"{{s.Id}}","Address":"{{s.Address}}","Port":8080},"Checks":[{"Status":"{{s.CheckStatus}}"}]}""")) + "]";

    private static async Task WriteJsonAsync(HttpContext ctx, string body)
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(body);
    }

    private static async Task StreamLineAsync(HttpContext ctx, string line)
    {
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.ContentType = "application/json";
        }

        await ctx.Response.WriteAsync(line + "\n");
        await ctx.Response.Body.FlushAsync();
    }

    private static async Task HoldOpenAsync(HttpContext ctx)
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.Body.FlushAsync();
        try
        {
            await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // client went away
        }
    }

    private static string NewServiceAccountDir(string token)
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-sa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "token"), token);
        File.WriteAllText(Path.Combine(dir, "namespace"), "prod");
        return dir;
    }

    private static void DeleteQuietly(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string? because = null)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.IsTrue(condition(), because ?? "condition was not met in time");
    }

    private sealed record FakeRequest(
        string Path, string Query, bool IsWatch, string? Authorization, string? ConsulToken);

    private sealed class FakeApi : IAsyncDisposable
    {
        private readonly IWebHost _host;

        private FakeApi(IWebHost host, string baseUrl, ConcurrentQueue<FakeRequest> requests)
        {
            _host = host;
            BaseUrl = baseUrl;
            RequestLog = requests;
        }

        public string BaseUrl { get; }

        private ConcurrentQueue<FakeRequest> RequestLog { get; }

        public FakeRequest[] Requests => RequestLog.ToArray();

        public static async Task<FakeApi> StartAsync(Func<HttpContext, Task> handler)
        {
            var log = new ConcurrentQueue<FakeRequest>();
            var host = new WebHostBuilder()
                .UseKestrel(o =>
                {
                    o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1);
                    o.AddServerHeader = false;
                })
                .Configure(app => app.Run(async ctx =>
                {
                    log.Enqueue(new FakeRequest(
                        ctx.Request.Path.Value ?? string.Empty,
                        ctx.Request.QueryString.Value ?? string.Empty,
                        ctx.Request.Query["watch"] == "true",
                        ctx.Request.Headers.Authorization.ToString() is { Length: > 0 } auth ? auth : null,
                        ctx.Request.Headers["X-Consul-Token"].ToString() is { Length: > 0 } token ? token : null));
                    await handler(ctx);
                }))
                .Build();
            await host.StartAsync();
            var feature = host.ServerFeatures.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
            var address = feature!.Addresses.First().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
            return new FakeApi(host, address.TrimEnd('/'), log);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _host.StopAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                // abandoned in-flight fake requests
            }

            _host.Dispose();
        }
    }
}
