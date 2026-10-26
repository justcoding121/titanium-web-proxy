using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Gateway status retry. H1 is covered here; H2 and H3 clients use the same origins.
/// </summary>
[TestClass]
public class StatusRetryTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task StatusRetry_ResetsFailureStreak_AfterRetried200()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
            Interlocked.Increment(ref hits) == 1 ? (503, "busy") : (200, "recovered"));
        var routes = new[]
        {
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Retry = new RetryConfig { Attempts = 1 },
            },
        };
        await using var started = await ProtocolMatrixTests.StartAsync(
            origin.Port, routes, ProtocolMatrixTests.ClientKind.H1Cleartext, destinations: 2);
        using var response = await ProtocolMatrixTests.SendAsync(
            started, ProtocolMatrixTests.ClientKind.H1Cleartext, "/retry");
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual(2, hits);

        var manager = started.Server.ReverseProxy!.ClusterManager!;
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("c1-d0"));
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("c1-d1"));
    }
}
