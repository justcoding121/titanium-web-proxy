#pragma warning disable TWP001
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.IntegrationTests.Helpers;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>Gateway retry v2 over HTTP/1 origins: status list, attempts, per-try timeout, replay safety.</summary>
[TestClass]
[DoNotParallelize]
public class StatusRetryV2Tests
{
    private const ProtocolMatrixTests.ClientKind Kind = ProtocolMatrixTests.ClientKind.H1Cleartext;

    [TestMethod]
    [Timeout(30_000)]
    public async Task TwoAttempts_ReachTheThirdDestination()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
            Interlocked.Increment(ref hits) < 3 ? (503, "busy") : (200, "third"));
        await using var started = await StartAsync(origin.Port, new RetryConfig { Attempts = 2 }, destinations: 3);

        using var response = await ProtocolMatrixTests.SendAsync(started, Kind, "/retry");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("third", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(3, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task OneAttempt_IsNotEnoughForTwoFailures()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
            Interlocked.Increment(ref hits) < 3 ? (503, "busy") : (200, "third"));
        await using var started = await StartAsync(origin.Port, new RetryConfig { Attempts = 1 }, destinations: 3);

        using var response = await ProtocolMatrixTests.SendAsync(started, Kind, "/retry");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(2, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CustomStatusList_RetriesA429()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
            Interlocked.Increment(ref hits) == 1 ? (429, "slow down") : (200, "ok"));
        await using var started = await StartAsync(
            origin.Port, new RetryConfig { Attempts = 1, StatusCodes = [429] });

        using var response = await ProtocolMatrixTests.SendAsync(started, Kind, "/retry");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DefaultStatusList_LeavesA429Alone()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
        {
            Interlocked.Increment(ref hits);
            return (429, "slow down");
        });
        await using var started = await StartAsync(origin.Port, new RetryConfig { Attempts = 2 });

        using var response = await ProtocolMatrixTests.SendAsync(started, Kind, "/retry");
        Assert.AreEqual((HttpStatusCode)429, response.StatusCode);
        Assert.AreEqual(1, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CustomStatusList_LeavesA503Alone()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
        {
            Interlocked.Increment(ref hits);
            return (503, "busy");
        });
        await using var started = await StartAsync(
            origin.Port, new RetryConfig { Attempts = 2, StatusCodes = [429] });

        using var response = await ProtocolMatrixTests.SendAsync(started, Kind, "/retry");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(1, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PerTryTimeout_MovesToTheSecondDestination()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
        {
            if (Interlocked.Increment(ref hits) == 1)
            {
                Thread.Sleep(4000);
                return (200, "late");
            }

            return (200, "prompt");
        });
        await using var started = await StartAsync(
            origin.Port, new RetryConfig { Attempts = 1, PerTryTimeoutMilliseconds = 400 });

        using var response = await ProtocolMatrixTests.SendAsync(started, Kind, "/retry");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("prompt", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(2, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NonIdempotentRequest_IsNotRetried_WithoutTheFlag()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
        {
            Interlocked.Increment(ref hits);
            return (503, "busy");
        });
        await using var started = await StartAsync(origin.Port, new RetryConfig { Attempts = 2 }, destinations: 3);
        using var client = ProxyClient(started);

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://matrix.test/retry")
        {
            Content = new StringContent(string.Empty),
        };
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(1, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task KeepAliveClient_RetriesEveryRequestIndependently()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
            Interlocked.Increment(ref hits) % 2 == 1 ? (503, "busy") : (200, "ok"));
        await using var started = await StartAsync(origin.Port, new RetryConfig { Attempts = 1 });
        using var client = ProxyClient(started);

        for (var i = 0; i < 3; i++)
        {
            using var response = await client.GetAsync("http://matrix.test/retry");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "request " + i);
        }

        Assert.AreEqual(6, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Budget_RefusesNothingWhenTheLimitIsRoomy_AndNeverLeaksASlot()
    {
        var hits = 0;
        await using var origin = new ProtocolMatrixTests.RecordingOrigin((_, _) =>
            Interlocked.Increment(ref hits) % 2 == 1 ? (503, "busy") : (200, "ok"));
        await using var started = await StartAsync(
            origin.Port, new RetryConfig { Attempts = 1, BudgetPercent = 20, BudgetMinConcurrent = 2 });
        using var client = ProxyClient(started);

        // A slot that leaked from each retried request would run the limit of two dry by the third.
        for (var i = 0; i < 6; i++)
        {
            using var response = await client.GetAsync("http://matrix.test/retry");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "request " + i);
        }

        Assert.AreEqual(12, hits);
    }

    private static HttpClient ProxyClient(ProtocolMatrixTests.Started started) =>
        new(new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy($"http://127.0.0.1:{started.Clear.Port}"),
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

    private static Task<ProtocolMatrixTests.Started> StartAsync(int originPort, RetryConfig retry, int destinations = 2)
    {
        var routes = new[]
        {
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Retry = retry,
            },
        };
        return ProtocolMatrixTests.StartAsync(originPort, routes, Kind, destinations: destinations);
    }
}
