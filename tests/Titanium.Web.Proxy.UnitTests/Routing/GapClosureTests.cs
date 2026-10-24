using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Configuration;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
[DoNotParallelize]
public class GapClosureTests
{
    [TestMethod]
    public void TemplateConstraints_AcceptKnownShapes_AndRejectUnknown()
    {
        var matcher = new RouteMatcher();
        Assert.IsNotNull(Match(matcher, "/users/{id:int}", "/users/15"));
        Assert.IsNull(Match(matcher, "/users/{id:int}", "/users/abc"));
        Assert.IsNotNull(Match(matcher, "/users/{id:guid}", "/users/" + Guid.NewGuid()));
        Assert.IsNotNull(Match(matcher, "/users/{name:alpha}", "/users/Ada"));
        Assert.IsNull(Match(matcher, "/users/{name:alpha}", "/users/A1"));
        Assert.IsNotNull(Match(matcher, "/users/{id:regex([0-9]+)}", "/users/9"));
        Assert.IsNull(Match(matcher, "/users/{id:nope}", "/users/9"));
    }

    [TestMethod]
    public void CompiledIndex_MatchesTheLinearScan()
    {
        var random = new Random(1108);
        var kinds = new[] { PathMatchKind.Exact, PathMatchKind.Prefix, PathMatchKind.SegmentPrefix, PathMatchKind.Template };
        var matcher = new RouteMatcher();
        for (var sample = 0; sample < 40; sample++)
        {
            var routes = new List<RouteConfig>();
            for (var i = 0; i < 8; i++)
            {
                var kind = kinds[random.Next(kinds.Length)];
                var path = kind switch
                {
                    PathMatchKind.Template => "/seg" + random.Next(3) + "/{id:int}",
                    PathMatchKind.Exact => "/seg" + random.Next(3) + "/item",
                    PathMatchKind.SegmentPrefix => "/seg" + random.Next(3),
                    _ => "/seg" + random.Next(3),
                };
                routes.Add(new RouteConfig
                {
                    Id = "r" + i,
                    ClusterId = "c",
                    Order = random.Next(3),
                    Match = new RouteMatch { Path = path, PathKind = kind },
                });
            }

            var frozen = (IReadOnlyList<RouteConfig>)routes;
            for (var path = 0; path < 6; path++)
            {
                var context = new RouteMatchContext(null, "/seg" + (path % 3) + (path % 2 == 0 ? "/item" : "/4"), "GET", null, null);
                var indexed = matcher.Match(context, frozen);
                var linear = matcher.MatchUnindexed(context, frozen);
                Assert.AreEqual(linear?.Id, indexed?.Id, context.Path);
            }
        }
    }

    [TestMethod]
    public async Task GatewayStatus_RetriesOnce_ForAnIdempotentRequest()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations =
                [
                    new DestinationConfig { Id = "a", Address = "10.0.0.1" },
                    new DestinationConfig { Id = "b", Address = "10.0.0.2" },
                ],
            },
        ]);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            ClusterManager = manager,
            LoadBalancer = new LoadBalancer(),
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Retry = new RetryConfig { Attempts = 1 },
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };

        var session = Session(proxy, "GET");
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Routed, ReverseProxySessionDispatch.Apply(proxy, session));
        var first = session.UpstreamDestinationId;
        session.HttpClient.Response.StatusCode = 503;
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));
        Assert.AreNotEqual(first, session.UpstreamDestinationId);
        Assert.IsFalse(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));

        var post = Session(proxy, "POST");
        post.HttpClient.Request.ContentLength = 4;
        ReverseProxySessionDispatch.Apply(proxy, post);
        post.HttpClient.Response.StatusCode = 502;
        Assert.IsFalse(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, post));
    }

    [TestMethod]
    public void RateWindows_AreEvictedWhenIdle()
    {
        RoutePolicies.ResetForTests();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        RoutePolicies.Clock = clock;
        var policy = new RoutePolicyConfig { Name = "r", Kind = "rateLimit", Limit = 10, WindowSeconds = 1 };
        var route = new RouteConfig
        {
            Id = "route",
            ClusterId = "c",
            Policies = ["r"],
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        };
        var options = new ReverseProxyOptions
        {
            Policies = [policy],
            Routes = [route],
            RouteMatcher = new RouteMatcher(),
        };
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false) { ForwardHost = "example.test" };
        bool Allowed(IPAddress address)
        {
            var request = new Request { Method = "GET", RequestUriString8 = (ByteString)"/", Host = "example.test" };
            return !RoutePolicies.TryHandleLite(options, endPoint, request, new IPEndPoint(address, 9), out _, out _, out _);
        }

        Assert.IsTrue(Allowed(IPAddress.Loopback));
        clock.Advance(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 64; i++)
        {
            Assert.IsTrue(Allowed(IPAddress.Parse("10.0.0." + (i % 20))));
        }

        Assert.IsTrue(RoutePolicies.RateWindowCount <= 20);
        RoutePolicies.ResetForTests();
    }

    [TestMethod]
    public void Deny_AfterAConnectionSlot_ReleasesTheSlot()
    {
        RoutePolicies.ResetForTests();
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var route = new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Policies = ["slots", "key"],
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        };
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Policies =
            [
                new RoutePolicyConfig { Name = "slots", Kind = "connectionLimit", Limit = 1 },
                new RoutePolicyConfig { Name = "key", Kind = "requireHeader", HeaderName = "X-Key", HeaderValue = "ok" },
            ],
            Routes = [route],
        };

        var denied = Session(proxy, "GET");
        Assert.IsTrue(RoutePolicies.TryDeny(proxy, denied, route));
        Assert.AreEqual(401, denied.HttpClient.Response.StatusCode);

        var allowed = Session(proxy, "GET");
        allowed.HttpClient.Request.Headers.AddHeader("X-Key", "ok");
        Assert.IsFalse(RoutePolicies.TryDeny(proxy, allowed, route));
        RoutePolicies.ResetForTests();
    }

    [TestMethod]
    public async Task Mirror_CopiesMethodPathHeadersAndBody_WithoutChangingTheClient()
    {
        MirrorFanout.ResetForTests();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync();
            var request = new Request { Method = "POST", Host = "app.test" };
            request.RequestUriString8 = (ByteString)"/copy";
            request.Headers.AddHeader("X-Trace", "abc");
            request.Body = Encoding.UTF8.GetBytes("ping");
            request.IsBodyRead = true;
            using var proxy = new ProxyServer(userTrustRootCertificate: false);
            var session = Session(proxy, "POST");
            session.HttpClient.Response.StatusCode = 200;
            MirrorFanout.TryEnqueue(
                new MirrorConfig { ClusterId = "m", MaxBodyBytes = 64 },
                request,
                new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = port });
            using var client = await accepted.WaitAsync(TimeSpan.FromSeconds(3));
            await using var stream = client.GetStream();
            var buffer = new byte[512];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read));
                if (n == 0)
                {
                    break;
                }

                read += n;
                if (Encoding.UTF8.GetString(buffer, 0, read).Contains("ping", StringComparison.Ordinal))
                {
                    break;
                }
            }

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            StringAssert.Contains(text, "POST /copy HTTP/1.1");
            StringAssert.Contains(text, "X-Trace: abc");
            StringAssert.Contains(text, "ping");
            Assert.AreEqual(200, session.HttpClient.Response.StatusCode);
            client.Close();
        }
        finally
        {
            listener.Stop();
            MirrorFanout.ResetForTests();
        }
    }

    [TestMethod]
    public void Mirror_SkipsALargeBody_AndDropsWhenTheQueueIsFull()
    {
        MirrorFanout.ResetForTests();
        var large = new Request { Method = "POST", Host = "app.test", ContentLength = 100_000 };
        large.RequestUriString8 = (ByteString)"/big";
        MirrorFanout.TryEnqueue(
            new MirrorConfig { ClusterId = "m", MaxBodyBytes = 32 },
            large,
            new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 9 });
        Assert.AreEqual(1, MirrorFanout.SkippedForTests);

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MirrorFanout.HoldForTests = hold.Task;
        try
        {
            var small = new Request { Method = "GET", Host = "app.test" };
            small.RequestUriString8 = (ByteString)"/q";
            MirrorFanout.TryEnqueue(
                new MirrorConfig { ClusterId = "m" },
                small,
                new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 9 });
            var until = DateTime.UtcNow.AddSeconds(2);
            while (MirrorFanout.HoldEntriesForTests == 0 && DateTime.UtcNow < until)
            {
                Thread.Sleep(1);
            }

            Assert.IsTrue(MirrorFanout.HoldEntriesForTests > 0, "mirror worker did not pause");
            for (var i = 0; i < 40; i++)
            {
                MirrorFanout.TryEnqueue(
                    new MirrorConfig { ClusterId = "m" },
                    small,
                    new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 9 });
            }

            Assert.IsTrue(MirrorFanout.DroppedForTests > 0, $"dropped={MirrorFanout.DroppedForTests} queued={MirrorFanout.QueuedForTests}");
            Assert.IsTrue(MirrorFanout.QueuedForTests <= 32, $"queued={MirrorFanout.QueuedForTests}");
        }
        finally
        {
            hold.TrySetResult();
            MirrorFanout.ResetForTests();
        }
    }

    [TestMethod]
    public async Task DnsPin_IsAppliedOnDispatch_AndClearedOnKeepAlive()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations = [new DestinationConfig { Id = "d", Address = "origin.test", Port = 80, DnsRefreshSeconds = 30 }],
            },
        ]);
        manager.SetResolved("d", IPAddress.Parse("10.1.2.3"));
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            ClusterManager = manager,
            LoadBalancer = new LoadBalancer(),
            Routes = [new RouteConfig { Id = "r", ClusterId = "c", Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix } }],
        };
        var session = Session(proxy, "GET");
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Routed, ReverseProxySessionDispatch.Apply(proxy, session));
        Assert.AreEqual(IPAddress.Parse("10.1.2.3"), session.UpstreamPinnedAddress);
        session.ResetForKeepAlive(null, null);
        Assert.IsNull(session.UpstreamPinnedAddress);
        Assert.IsNull(session.UpstreamHttpClient);
        Assert.IsNull(session.Extras);
    }

    [TestMethod]
    public async Task ConcurrentReloads_PublishCompleteSnapshots()
    {
        var host = new ProxyConfigHost();
        var manager = new ClusterManager();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? published = null;
        var first = Task.Run(() => host.ApplyAsync(Snapshot("first"), manager, options =>
        {
            published = options.Routes![0].Id;
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }));
        await started.Task;
        var second = host.ApplyAsync(Snapshot("second"), manager, options =>
        {
            published = options.Routes![0].Id;
        });
        release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.AreEqual("second", published);
        Assert.IsTrue(manager.Snapshot.Clusters.ContainsKey("second"));
        Assert.IsFalse(manager.Snapshot.Clusters.ContainsKey("first"));
    }

    [TestMethod]
    public void ClusterTimeouts_ChangeThePoolKey_OnlyWhenSet()
    {
        Assert.AreEqual(string.Empty, new ClusterHttpClientConfig().ConnectionKey);
        var left = new ClusterHttpClientConfig { ConnectTimeout = TimeSpan.FromSeconds(1) };
        var right = new ClusterHttpClientConfig { ConnectTimeout = TimeSpan.FromSeconds(2) };
        Assert.AreNotEqual(left.ConnectionKey, right.ConnectionKey);
        Assert.AreNotEqual(string.Empty, left.ConnectionKey);
    }

    [TestMethod]
    public void Trailers_AndCustomTransforms_StayOffUntilConfigured()
    {
        var engine = new TransformEngine();
        var context = new TransformRequestContext { Path = "/" };
        engine.ApplyRequestTransforms(
        [
            new TransformConfig
            {
                Kind = TransformKinds.RequestTrailerSet,
                Parameters = new Dictionary<string, string> { ["name"] = "X-Trace", ["value"] = "1" },
            },
        ], context);
        Assert.AreEqual("1", context.RequestTrailers!["X-Trace"]);

        var custom = new TransformEngine
        {
            Factories = [new StampFactory()],
        };
        var again = new TransformRequestContext { Path = "/old" };
        custom.ApplyRequestTransforms(
            [new TransformConfig { Kind = "stamp" }],
            again);
        Assert.AreEqual("/stamped", again.Path);
    }

    [TestMethod]
    public async Task Validator_RejectsTheSnapshot()
    {
        var host = new ProxyConfigHost(validators: [new RejectAll()]);
        var manager = new ClusterManager();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => host.ApplyAsync(
            new ProxyConfigSnapshot(),
            manager,
            _ => { }));
    }

    private static ProxyConfigSnapshot Snapshot(string id) => new()
    {
        Routes = [new RouteConfig { Id = id, ClusterId = id, Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix } }],
        Clusters = [new ClusterConfig { Id = id, Destinations = [new DestinationConfig { Id = id, Address = "127.0.0.1", Port = 9 }] }],
    };

    private static RouteConfig? Match(RouteMatcher matcher, string template, string path) =>
        matcher.Match(
            new RouteMatchContext(null, path, "GET", null, null),
            [new RouteConfig { Id = "r", ClusterId = "c", Match = new RouteMatch { Path = template, PathKind = PathMatchKind.Template } }]);

    private static SessionEventArgs Session(ProxyServer proxy, string method)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var stream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, stream, null, cts);
        session.HttpClient.Request.Method = method;
        session.HttpClient.Request.RequestUriString = "http://example.test/";
        return session;
    }

    private sealed class StampFactory : IRouteTransformFactory
    {
        public bool CanHandle(string kind) => kind == "stamp";

        public void Apply(TransformConfig transform, TransformRequestContext context) => context.Path = "/stamped";
    }

    private sealed class RejectAll : IProxyConfigValidator
    {
        public IReadOnlyList<string> Validate(ProxyConfigSnapshot snapshot) => ["no"];
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
