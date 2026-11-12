using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Control;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: GatewayController.RunAsync cancel path, gRPC match-without-method
///     PathPrefix, L4 foreign parent kind skip, Hostnames.Rank / Selectors Exists miss.
/// </summary>
[TestClass]
public class GatewayControllerRunAndGrpcMatchSoftCoverageBehaviorTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task Controller_RunAsync_WithLeader_CancelsCleanly()
    {
        await using var fixture = new ClusterFixture();
        var cache = new ClusterCache(fixture.Api, new ClusterCacheOptions(), NullLogger.Instance);
        var options = ClusterFixture.Options(TimeSpan.FromMilliseconds(20));
        var leader = new LeaderElector(
            fixture.Api,
            new LeaderElectionOptions { Namespace = "ns", Identity = "run-a", Name = "run-lease" },
            NullLogger.Instance);
        var controller = new GatewayController(
            cache,
            new OutputWriter(fixture.Api, options.Output),
            new StatusWriter(fixture.Api, ClusterFixture.Controller, NullLogger.Instance),
            fixture.Observer,
            leader,
            options,
            NullLogger.Instance,
            events: new EventWriter(fixture.Api, ClusterFixture.Controller, NullLogger.Instance));

        using var stop = new CancellationTokenSource();
        var run = controller.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced && controller.Ready);
            controller.Signal();
            await ClusterFixture.WaitForAsync(() => leader.IsLeader || controller.IsLeader);
            Assert.IsTrue(controller.Ready);
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
    public void GrpcRoute_MatchWithoutMethod_IsPathPrefixRoot()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            new GrpcRouteRule
            {
                Matches = [new GrpcRouteMatch()],
                BackendRefs = [new HttpBackendRef { Name = "web", Port = 8080 }],
            }));
        var result = b.Render();
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));
        var route = result.Config.UserRoutes().Single();
        Assert.AreEqual(PathMatchKind.Prefix, route.Match!.PathKind);
        // Match-without-method becomes a root PathPrefix ("/" or empty depending on serializer).
        Assert.IsTrue(string.IsNullOrEmpty(route.Match.Path) || route.Match.Path == "/");
    }

    [TestMethod]
    public void L4_ForeignParentKind_IsIgnored()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        var route = TcpRoute("r", "default", Backend("web"));
        route.Spec.ParentRefs = [new ParentReference { Name = "gw", Group = "example.com", Kind = "Gateway" }];
        b.TcpRoutes.Add(route);
        var result = b.Render();
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count);
        Assert.IsFalse(result.StatusPatches.Any(p => p.Kind == StatusKind.TcpRoute));
    }

    [TestMethod]
    public void HostnamesRank_AndSelectorsExistsMiss()
    {
        Assert.AreEqual(0, Hostnames.Rank("exact.example"));
        Assert.AreEqual(1, Hostnames.Rank("*.example"));
        Assert.AreEqual(2, Hostnames.Rank(null));
        Assert.AreEqual(2, Hostnames.Rank(""));

        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["app"] = "web" };
        Assert.IsFalse(Selectors.Matches(
            new LabelSelector
            {
                MatchExpressions = [new LabelSelectorRequirement { Key = "missing", Operator = "Exists" }],
            },
            labels));
        Assert.IsTrue(Selectors.Matches(
            new LabelSelector
            {
                MatchExpressions = [new LabelSelectorRequirement { Key = "missing", Operator = "DoesNotExist" }],
            },
            labels));
        Assert.IsTrue(Hostnames.TryIntersect("*.svc.example", "*.example", out var nested));
        Assert.AreEqual("*.svc.example", nested);
    }
}
