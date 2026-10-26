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

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_StatusRetry_FailsOverToSecondDestination()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
            Interlocked.Increment(ref hits) == 1 ? (503, "busy") : (200, "recovered"));
        var before = 0;
        var after = 0;
        await using var started = await StartRetryProxy(origin.Port, attempts: 1);
        started.Server.BeforeResponse += (_, _) =>
        {
            Interlocked.Increment(ref before);
            return Task.CompletedTask;
        };
        started.Server.AfterResponse += (_, _) =>
        {
            Interlocked.Increment(ref after);
            return Task.CompletedTask;
        };

        using var response = await ProtocolMatrixTests.SendAsync(
            started, ProtocolMatrixTests.ClientKind.H2Tls, "/retry");
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("recovered", body);
        Assert.AreEqual(2, hits);
        Assert.AreEqual(1, before);
        Assert.AreEqual(1, after);
        var manager = started.Server.ReverseProxy!.ClusterManager!;
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("c1-d0"));
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("c1-d1"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_StatusRetry_BothDestinationsFail_ClientSeesOne503()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
        {
            Interlocked.Increment(ref hits);
            return (503, "busy");
        });
        await using var started = await StartRetryProxy(origin.Port, attempts: 1);
        using var response = await ProtocolMatrixTests.SendAsync(
            started, ProtocolMatrixTests.ClientKind.H2Tls, "/retry");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(2, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_StatusRetry_NotWhenAttemptsZero()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
        {
            Interlocked.Increment(ref hits);
            return (503, "busy");
        });
        await using var started = await StartRetryProxy(origin.Port, attempts: 0);
        using var response = await ProtocolMatrixTests.SendAsync(
            started, ProtocolMatrixTests.ClientKind.H2Tls, "/retry");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(1, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_StatusRetry_NotWhenNoAlternate()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
        {
            Interlocked.Increment(ref hits);
            return (503, "busy");
        });
        await using var started = await StartRetryProxy(origin.Port, attempts: 1, destinations: 1);
        using var response = await ProtocolMatrixTests.SendAsync(
            started, ProtocolMatrixTests.ClientKind.H2Tls, "/retry");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(1, hits);
    }

    private static Task<ProtocolMatrixTests.Started> StartRetryProxy(int originPort, int attempts, int destinations = 2)
    {
        var routes = new[]
        {
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Retry = attempts > 0 ? new RetryConfig { Attempts = attempts } : null,
            },
        };
        return ProtocolMatrixTests.StartAsync(
            originPort, routes, ProtocolMatrixTests.ClientKind.H2Tls, destinations: destinations);
    }
}
