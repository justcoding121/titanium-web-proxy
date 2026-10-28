using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Configuration;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Certificates;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.UnitTests.Support;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class ParityGapTests
{
    [TestCleanup]
    public void Cleanup()
    {
        TrafficSplit.Draw = null;
        RoutePolicies.ResetForTests();
    }

    [TestMethod]
    public void TrafficSplit_UsesTheDrawAndAnOverrideHeader()
    {
        var request = new Request();
        var route = SplitRoute();
        TrafficSplit.Draw = _ => 0;
        Assert.AreEqual("a", TrafficSplit.Select(route, request));
        TrafficSplit.Draw = total => total - 1;
        Assert.AreEqual("b", TrafficSplit.Select(route, request));
        request.Headers.AddHeader("X-Cluster", "b");
        TrafficSplit.Draw = _ => 0;
        Assert.AreEqual("b", TrafficSplit.Select(route, request));
    }

    [TestMethod]
    public void TrafficSplit_DistributionStaysWithinFivePercent()
    {
        var request = new Request();
        var route = SplitRoute();
        var a = 0;
        for (var i = 0; i < 10000; i++)
        {
            if (TrafficSplit.Select(route, request) == "a")
            {
                a++;
            }
        }

        Assert.IsTrue(a is > 2000 and < 3000, a.ToString());
    }

    [TestMethod]
    public void ErrorPage_ReplacesThe503Body()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            ErrorPages = [new ErrorPageConfig { Status = "503", ContentType = "text/plain", Body = "down" }],
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "missing",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        var session = MakeSession(proxy);
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.NoDestination, ReverseProxySessionDispatch.Apply(proxy, session));
        ReverseProxySessionDispatch.SignalNoDestination(session);
        Assert.AreEqual("down", Encoding.UTF8.GetString(session.HttpClient.Response.Body));
    }

    [TestMethod]
    public void Redirect_HttpToHttpsAndDirectHeaders()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "",
                    Redirect = new RedirectConfig { Host = "example.test", Path = "/new", HttpToHttps = true },
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        var session = MakeSession(proxy);
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Handled, ReverseProxySessionDispatch.Apply(proxy, session));
        Assert.AreEqual("https://example.test/new?x=1", session.HttpClient.Response.Headers.GetHeaders("Location")![0].Value);

        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "d",
                    ClusterId = "",
                    DirectResponse = new DirectResponseConfig
                    {
                        Status = 200,
                        Body = "ok",
                        Headers = new Dictionary<string, string> { ["X-Test"] = "1" },
                    },
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        session = MakeSession(proxy);
        ReverseProxySessionDispatch.Apply(proxy, session);
        Assert.AreEqual("1", session.HttpClient.Response.Headers.GetHeaders("X-Test")![0].Value);
    }

    [TestMethod]
    public async Task Affinity_PinsTheOpaqueKeyAndCanReturn503()
    {
        var manager = new ClusterManager();
        var cluster = new ClusterConfig
        {
            Id = "c",
            SessionAffinity = new SessionAffinityConfig { CookieName = "sticky", FailurePolicy = AffinityFailurePolicy.Return503 },
            Destinations =
            [
                new DestinationConfig { Id = "a", Address = "10.0.0.1", AffinityKey = "pin-a" },
                new DestinationConfig { Id = "b", Address = "10.0.0.2", AffinityKey = "pin-b" },
            ],
        };
        await manager.ApplyAsync([cluster]);
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ReverseProxy = Options(manager, cluster);
        var session = MakeSession(proxy);
        session.HttpClient.Request.Headers.AddHeader("Cookie", "sticky=pin-b");
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Routed, ReverseProxySessionDispatch.Apply(proxy, session));
        Assert.AreEqual("b", session.UpstreamDestinationId);

        Assert.IsTrue(manager.TrySetDestinationState("b", DestinationState.Healthy, DestinationState.Unhealthy));
        session = MakeSession(proxy);
        session.HttpClient.Request.Headers.AddHeader("Cookie", "sticky=pin-b");
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.NoDestination, ReverseProxySessionDispatch.Apply(proxy, session));
    }

    [TestMethod]
    public async Task HashKey_IsStableForTheSamePath()
    {
        var manager = new ClusterManager();
        var cluster = new ClusterConfig
        {
            Id = "c",
            Algorithm = LoadBalanceAlgorithm.ConsistentHash,
            HashKey = new HashKeyConfig { Source = "path" },
            Destinations =
            [
                new DestinationConfig { Id = "a", Address = "10.0.0.1" },
                new DestinationConfig { Id = "b", Address = "10.0.0.2" },
            ],
        };
        await manager.ApplyAsync([cluster]);
        var lb = new LoadBalancer();
        var snapshot = manager.Snapshot;
        var first = lb.Select(cluster, snapshot, new LoadBalanceContext(null, "/same"));
        var second = lb.Select(cluster, snapshot, new LoadBalanceContext(null, "/same"));
        Assert.AreEqual(first!.Id, second!.Id);
    }

    [TestMethod]
    public async Task CustomPolicy_PicksTheNamedDestination()
    {
        var manager = new ClusterManager();
        var cluster = new ClusterConfig
        {
            Id = "c",
            LoadBalancingPolicy = "last",
            Destinations =
            [
                new DestinationConfig { Id = "a", Address = "10.0.0.1" },
                new DestinationConfig { Id = "b", Address = "10.0.0.2" },
            ],
        };
        await manager.ApplyAsync([cluster]);
        var lb = new LoadBalancer { Policies = [new LastPolicy()] };
        Assert.AreEqual("b", lb.Select(cluster, manager.Snapshot)!.Id);
    }

    [TestMethod]
    public void SniSelector_PrefersExactThenTheLongestWildcard()
    {
        using var exact = MakeCert("exact");
        using var wide = MakeCert("wide");
        using var narrow = MakeCert("narrow");
        var bindings = new List<SniCertificateBinding>
        {
            new() { Hosts = ["*.example.com"], Certificate = wide },
            new() { Hosts = ["*.a.example.com"], Certificate = narrow },
            new() { Hosts = ["www.example.com"], Certificate = exact },
        };
        Assert.AreSame(exact, SniCertificateSelector.Select("www.example.com", bindings));
        Assert.AreSame(narrow, SniCertificateSelector.Select("api.a.example.com", bindings));
        Assert.AreSame(wide, SniCertificateSelector.Select("www.other.example.com", bindings));
        Assert.IsNull(SniCertificateSelector.Select("example.com", bindings));
    }

    [TestMethod]
    public void ProxyProtocolV1_ReadsTheClientAddress()
    {
        var bytes = Encoding.ASCII.GetBytes("PROXY TCP4 203.0.113.1 203.0.113.2 12345 443\r\n");
        Assert.IsTrue(ProxyProtocolReader.TryParse(bytes, out var client, out var consumed));
        Assert.AreEqual(bytes.Length, consumed);
        Assert.AreEqual("203.0.113.1", client!.Address.ToString());
        Assert.AreEqual(12345, client.Port);
    }

    [TestMethod]
    public async Task Passthrough_CopiesBytesToTheUpstreamSocket()
    {
        var origin = new TcpListener(IPAddress.Loopback, 0);
        var door = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        door.Start();
        try
        {
            var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
            var accepted = door.AcceptSocketAsync();
            using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync((IPEndPoint)door.LocalEndpoint);
            using var inbound = await accepted;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var relay = TcpPassthrough.RelayAsync(inbound, "127.0.0.1", originPort, cts.Token);
            using var originClient = await origin.AcceptTcpClientAsync(cts.Token);
            await client.SendAsync(Encoding.ASCII.GetBytes("ping"), cts.Token);
            var buffer = new byte[4];
            var read = await originClient.GetStream().ReadAsync(buffer, cts.Token);
            Assert.AreEqual("ping", Encoding.ASCII.GetString(buffer, 0, read));
            client.Close();

            // The client's EOF reaches the origin as a half-close. The relay ends once the origin answers it.
            Assert.AreEqual(0, await originClient.GetStream().ReadAsync(buffer, cts.Token));
            originClient.Close();
            await relay;
        }
        finally
        {
            origin.Stop();
            door.Stop();
        }
    }

    [TestMethod]
    public async Task Policies_RateLimitApiKeyAndAddress()
    {
        var clock = new ManualTimeProvider();
        RoutePolicies.Clock = clock;
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Policies =
            [
                new RoutePolicyConfig { Name = "rate", Kind = "rateLimit", Limit = 1, WindowSeconds = 10 },
                new RoutePolicyConfig { Name = "key", Kind = "apiKey", HeaderName = "X-Api-Key", HeaderValue = "secret" },
                new RoutePolicyConfig { Name = "net", Kind = "ipAllowDeny", Deny = "127.0.0.1/32" },
            ],
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "missing",
                    Policies = ["rate"],
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.NoDestination, ReverseProxySessionDispatch.Apply(proxy, MakeSession(proxy)));
        var limited = MakeSession(proxy);
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Handled, ReverseProxySessionDispatch.Apply(proxy, limited));
        Assert.AreEqual(429, limited.HttpClient.Response.StatusCode);
        Assert.AreEqual(Abstractions.Diagnostics.ForwarderError.PolicyDenied, limited.ForwarderError);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.NoDestination, ReverseProxySessionDispatch.Apply(proxy, MakeSession(proxy)));

        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Policies = [new RoutePolicyConfig { Name = "key", Kind = "apiKey", HeaderName = "X-Api-Key", HeaderValue = "secret" }],
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "missing",
                    Policies = ["key"],
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        var denied = MakeSession(proxy);
        denied.HttpClient.Request.Headers.AddHeader("X-Api-Key", "nope");
        Assert.AreEqual(401, ApplyStatus(proxy, denied));

        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Policies = [new RoutePolicyConfig { Name = "net", Kind = "ipAllowDeny", Deny = "127.0.0.1/32" }],
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "missing",
                    Policies = ["net"],
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        Assert.AreEqual(403, ApplyStatus(proxy, MakeSession(proxy)));
    }

    [TestMethod]
    public void CorsPreflight_DoesNotContactACluster()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Policies = [new RoutePolicyConfig { Name = "cors", Kind = "cors", Origins = "https://app.test", Methods = "GET" }],
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "missing",
                    Policies = ["cors"],
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        var session = MakeSession(proxy);
        session.HttpClient.Request.Method = "OPTIONS";
        session.HttpClient.Request.Headers.AddHeader("Origin", "https://app.test");
        Assert.AreEqual(204, ApplyStatus(proxy, session));
        Assert.AreEqual("https://app.test", session.HttpClient.Response.Headers.GetHeaders("Access-Control-Allow-Origin")![0].Value);
    }

    [TestMethod]
    public async Task Mirror_NotifiesTheSinkWithoutFailingTheRoute()
    {
        var seen = new List<string>();
        var manager = new ClusterManager();
        var cluster = new ClusterConfig
        {
            Id = "c",
            Destinations = [new DestinationConfig { Id = "a", Address = "10.0.0.1" }],
        };
        await manager.ApplyAsync([cluster]);
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.Mirror.Observed = (clusterId, _, _) => seen.Add(clusterId);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            ClusterManager = manager,
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Mirror = new MirrorConfig { ClusterId = "copy" },
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Routed, ReverseProxySessionDispatch.Apply(proxy, MakeSession(proxy)));
        Assert.AreEqual("copy", seen[0]);
    }

    [TestMethod]
    public async Task ConfigFilter_ThrowKeepsThePreviousSnapshot()
    {
        var manager = new ClusterManager();
        var host = new ProxyConfigHost([new ThrowingFilter()]);
        var published = false;
        var snapshot = new ProxyConfigSnapshot
        {
            Routes = [new RouteConfig { Id = "r", ClusterId = "c", Match = new RouteMatch { Path = "/" } }],
            Clusters = [new ClusterConfig { Id = "c", Destinations = [new DestinationConfig { Id = "a", Address = "10.0.0.1" }] }],
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            host.ApplyAsync(snapshot, manager, _ => published = true));
        Assert.IsFalse(published);
    }

    [TestMethod]
    public async Task DrainAsync_ReturnsWhenTheProxyIsNotRunning()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        await proxy.DrainAsync(TimeSpan.Zero);
        Assert.IsFalse(proxy.ProxyRunning);
    }

    private static int ApplyStatus(ProxyServer proxy, SessionEventArgs session)
    {
        ReverseProxySessionDispatch.Apply(proxy, session);
        return session.HttpClient.Response.StatusCode;
    }

    private static ReverseProxyOptions Options(ClusterManager manager, ClusterConfig cluster) => new()
    {
        ClusterManager = manager,
        Routes =
        [
            new RouteConfig
            {
                Id = "r",
                ClusterId = cluster.Id,
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        ],
    };

    private static RouteConfig SplitRoute() => new()
    {
        Id = "r",
        ClusterId = "a",
        SplitOverrideHeader = "X-Cluster",
        Clusters =
        [
            new WeightedCluster { ClusterId = "a", Weight = 1 },
            new WeightedCluster { ClusterId = "b", Weight = 3 },
        ],
        Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
    };

    private static SessionEventArgs MakeSession(ProxyServer proxy)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "http://old.test/old?x=1";
        return session;
    }

    private static X509Certificate2 MakeCert(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class LastPolicy : ILoadBalancingPolicy
    {
        public string Name => "last";

        public DestinationConfig? Pick(ClusterConfig cluster, IReadOnlyList<DestinationConfig> eligible, LoadBalanceContext context) =>
            eligible[eligible.Count - 1];
    }

    private sealed class ThrowingFilter : IProxyConfigFilter
    {
        public ValueTask<ProxyConfigSnapshot> ApplyAsync(ProxyConfigSnapshot snapshot, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("rejected");
    }
}
