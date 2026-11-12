using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary>
///     Every public property of a route, cluster, or destination is either ineligible for the ForwardHost fast path
///     or explicitly inert. A new property fails this test until someone classifies it.
/// </summary>
[TestClass]
public class ForwardHostEligibilityGuardTests
{
    private enum Expect
    {
        Ineligible,
        Inert,
    }

    [TestMethod]
    public void RouteMatch_EveryPropertyIsClassified()
    {
        var table = new Dictionary<string, (object? Value, Expect Expect)>(StringComparer.Ordinal)
        {
            ["Host"] = ("app.example", Expect.Ineligible),
            ["Path"] = ("/api", Expect.Ineligible),
            ["PathKind"] = (PathMatchKind.Exact, Expect.Ineligible),
            ["Method"] = ("GET", Expect.Ineligible),
            ["Headers"] = (new Dictionary<string, string> { ["x-a"] = "1" }, Expect.Ineligible),
            ["Query"] = (new Dictionary<string, string> { ["q"] = "1" }, Expect.Ineligible),
            ["QueryParameters"] = (new QueryParameterMatch[] { new() { Name = "q", Value = "1" } }, Expect.Ineligible),
            ["HeaderMatches"] = (new HeaderValueMatch[] { new() { Name = "x-a", Value = "1" } }, Expect.Ineligible),
            ["Hosts"] = (new[] { "app.example" }, Expect.Ineligible),
            ["Methods"] = (new[] { "GET" }, Expect.Ineligible),
            ["PathCaseSensitive"] = (false, Expect.Inert),
        };

        AssertClassified(typeof(RouteMatch), table, (name, value) =>
        {
            var match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix };
            typeof(RouteMatch).GetProperty(name)!.SetValue(match, value);
            return RouteMatcher.IsUnconstrained(match);
        });
    }

    [TestMethod]
    public async Task RouteClusterAndDestination_EveryPropertyIsClassified()
    {
        var routeTable = new Dictionary<string, (object? Value, Expect Expect)>(StringComparer.Ordinal)
        {
            ["Id"] = ("other", Expect.Inert),
            ["ClusterId"] = ("missing", Expect.Ineligible),
            ["Match"] = (new RouteMatch { Path = "/api", PathKind = PathMatchKind.Prefix }, Expect.Ineligible),
            ["Order"] = (4, Expect.Inert),
            ["Transforms"] = (new TransformConfig[] { new() { Kind = TransformKinds.PathPrefix } }, Expect.Ineligible),
            ["Limits"] = (new RouteLimits { RequestTimeoutSeconds = 5 }, Expect.Ineligible),
            ["Retry"] = (new RetryConfig { Attempts = 1 }, Expect.Ineligible),
            ["Redirect"] = (new RedirectConfig(), Expect.Ineligible),
            ["DirectResponse"] = (new DirectResponseConfig(), Expect.Ineligible),
            ["Policies"] = (new[] { "rate" }, Expect.Inert),
            ["Clusters"] = (new WeightedCluster[] { new() { ClusterId = "c" } }, Expect.Ineligible),
            ["SplitOverrideHeader"] = ("x-split", Expect.Inert),
            ["SplitOverrideCookie"] = ("split", Expect.Inert),
            ["Mirror"] = (new MirrorConfig { ClusterId = "c" }, Expect.Ineligible),
            ["ErrorPages"] = (new ErrorPageConfig[] { new() { Status = "503" } }, Expect.Ineligible),
            ["Compression"] = ("off", Expect.Inert),
            ["GrpcWeb"] = (true, Expect.Ineligible),
            ["Middleware"] = (new[] { "plugin" }, Expect.Ineligible),
        };
        var clusterTable = new Dictionary<string, (object? Value, Expect Expect)>(StringComparer.Ordinal)
        {
            ["Id"] = ("other", Expect.Ineligible),
            ["Destinations"] = (new DestinationConfig[] { Dest() }, Expect.Inert),
            ["Algorithm"] = (LoadBalanceAlgorithm.Random, Expect.Inert),
            ["AffinityCookie"] = ("sticky", Expect.Inert),
            ["AffinityHeader"] = ("x-pin", Expect.Inert),
            ["HealthCheck"] = (new HealthCheckConfig { Passive = new PassiveHealthConfig { Enabled = true } }, Expect.Ineligible),
            ["Retry"] = (new RetryConfig { Attempts = 1 }, Expect.Ineligible),
            ["SlowStartSeconds"] = (5, Expect.Ineligible),
            ["Queue"] = (new QueueConfig(), Expect.Ineligible),
            ["HttpClient"] = (new ClusterHttpClientConfig { AcceptAnyServerCertificate = true }, Expect.Ineligible),
            ["SessionAffinity"] = (new SessionAffinityConfig(), Expect.Ineligible),
            ["HashKey"] = (new HashKeyConfig(), Expect.Ineligible),
            ["LoadBalancingPolicy"] = ("custom", Expect.Ineligible),
            ["SendProxyProtocol"] = ("v2", Expect.Inert),
            ["ZoneAware"] = (new ZoneAwareConfig(), Expect.Inert),
        };
        var destinationTable = new Dictionary<string, (object? Value, Expect Expect)>(StringComparer.Ordinal)
        {
            ["Id"] = ("other", Expect.Inert),
            ["Address"] = ("10.0.0.9", Expect.Ineligible),
            ["Port"] = (81, Expect.Ineligible),
            ["UseHttps"] = (true, Expect.Inert),
            ["Weight"] = (2, Expect.Inert),
            ["UseDestinationHost"] = (true, Expect.Ineligible),
            ["Health"] = ("http://probe.example/health", Expect.Inert),
            ["DnsRefreshSeconds"] = (30, Expect.Ineligible),
            ["Host"] = ("probe.example", Expect.Inert),
            ["MaxConcurrentRequests"] = (4, Expect.Ineligible),
            ["Priority"] = (1, Expect.Ineligible),
            ["Backup"] = (true, Expect.Ineligible),
            ["SlowStartSeconds"] = (3, Expect.Ineligible),
            ["AffinityKey"] = ("pin", Expect.Inert),
            ["Zone"] = ("a", Expect.Inert),
        };
        var httpTable = new Dictionary<string, (object? Value, Expect Expect)>(StringComparer.Ordinal)
        {
            ["SslProtocols"] = ("Tls12", Expect.Ineligible),
            ["AcceptAnyServerCertificate"] = (true, Expect.Ineligible),
            ["MaxConnectionsPerServer"] = (4, Expect.Ineligible),
            ["ConnectTimeout"] = (TimeSpan.FromSeconds(2), Expect.Ineligible),
            ["RequestTimeout"] = (TimeSpan.FromSeconds(2), Expect.Ineligible),
            ["ClientCertificatePath"] = ("missing.pem", Expect.Ineligible),
            ["ClientCertificateKeyPath"] = ("missing.key", Expect.Inert),
            ["ClientCertificatePasswordEnv"] = ("CERT_PASSWORD", Expect.Inert),
            ["CaBundlePath"] = ("missing.crt", Expect.Ineligible),
            ["ServerName"] = ("origin.example", Expect.Ineligible),
            ["VerifyHostname"] = (false, Expect.Ineligible),
            ["ConnectionKey"] = (null, Expect.Inert),
        };

        await AssertRoute(routeTable);
        await AssertCluster(clusterTable);
        await AssertDestination(destinationTable);
        await AssertHttpClient(httpTable);

        var activeOnly = await SnapshotAsync(new ClusterConfig
        {
            Id = "c",
            Destinations = [Dest()],
            HealthCheck = new HealthCheckConfig { Active = new ActiveHealthConfig { Enabled = true } },
        });
        Assert.IsTrue(
            ReverseProxyFastPath.IsForwardHostEquivalent([Plain()], activeOnly, "10.0.0.1", 80),
            "active health alone stays eligible; the snapshot state already gates a bad destination");
    }

    private static async Task AssertRoute(Dictionary<string, (object? Value, Expect Expect)> table)
    {
        var snapshot = await SnapshotAsync(Cluster());
        AssertClassified(typeof(RouteConfig), table, (name, value) =>
        {
            var route = Plain();
            typeof(RouteConfig).GetProperty(name)!.SetValue(route, value);
            return ReverseProxyFastPath.IsForwardHostEquivalent([route], snapshot, "10.0.0.1", 80);
        });
    }

    private static async Task AssertCluster(Dictionary<string, (object? Value, Expect Expect)> table)
    {
        AssertClassified(typeof(ClusterConfig), table, (name, value) =>
        {
            var cluster = Cluster();
            typeof(ClusterConfig).GetProperty(name)!.SetValue(cluster, value);
            var snapshot = SnapshotAsync(cluster).GetAwaiter().GetResult();
            return ReverseProxyFastPath.IsForwardHostEquivalent([Plain()], snapshot, "10.0.0.1", 80);
        });
        await Task.CompletedTask;
    }

    private static async Task AssertDestination(Dictionary<string, (object? Value, Expect Expect)> table)
    {
        AssertClassified(typeof(DestinationConfig), table, (name, value) =>
        {
            var dest = Dest();
            typeof(DestinationConfig).GetProperty(name)!.SetValue(dest, value);
            var snapshot = SnapshotAsync(new ClusterConfig { Id = "c", Destinations = [dest] }).GetAwaiter().GetResult();
            return ReverseProxyFastPath.IsForwardHostEquivalent([Plain()], snapshot, "10.0.0.1", 80);
        });
        await Task.CompletedTask;
    }

    private static async Task AssertHttpClient(Dictionary<string, (object? Value, Expect Expect)> table)
    {
        AssertClassified(typeof(ClusterHttpClientConfig), table, (name, value) =>
        {
            var http = new ClusterHttpClientConfig();
            var property = typeof(ClusterHttpClientConfig).GetProperty(name)!;
            if (property.CanWrite)
            {
                property.SetValue(http, value);
            }

            var snapshot = SnapshotAsync(new ClusterConfig { Id = "c", Destinations = [Dest()], HttpClient = http })
                .GetAwaiter().GetResult();
            return ReverseProxyFastPath.IsForwardHostEquivalent([Plain()], snapshot, "10.0.0.1", 80);
        });
        await Task.CompletedTask;
    }

    private static void AssertClassified(
        Type type,
        Dictionary<string, (object? Value, Expect Expect)> table,
        Func<string, object?, bool> eligible)
    {
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            Assert.IsTrue(table.ContainsKey(property.Name), type.Name + "." + property.Name + " is not classified");
            if (!property.CanWrite)
            {
                continue;
            }

            var (value, expect) = table[property.Name];
            var isEligible = eligible(property.Name, value);
            if (expect == Expect.Ineligible)
            {
                Assert.IsFalse(isEligible, type.Name + "." + property.Name + " should leave the fast path");
            }
            else
            {
                Assert.IsTrue(isEligible, type.Name + "." + property.Name + " should stay on the fast path");
            }
        }
    }

    private static RouteConfig Plain() =>
        new() { Id = "r", ClusterId = "c", Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix } };

    private static DestinationConfig Dest() => new() { Id = "d", Address = "10.0.0.1", Port = 80 };

    private static ClusterConfig Cluster() => new() { Id = "c", Destinations = [Dest()] };

    private static async Task<ImmutableClusterSnapshot> SnapshotAsync(ClusterConfig cluster)
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync([cluster]);
        return manager.Snapshot;
    }
}
