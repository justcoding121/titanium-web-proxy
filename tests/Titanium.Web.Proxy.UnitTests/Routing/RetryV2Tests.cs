using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary>Retry v2: status list, attempts, backoff, Retry-After, budget, per-try timeout, replay safety.</summary>
[TestClass]
[DoNotParallelize]
public class RetryV2Tests
{
    [TestMethod]
    public async Task TwoAttempts_VisitThreeDestinations_ThenStop()
    {
        var (proxy, _) = await StartAsync(new RetryConfig { Attempts = 2 });
        using var _p = proxy;
        var session = Session(proxy, "GET");
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Routed, ReverseProxySessionDispatch.Apply(proxy, session));
        var seen = new List<string?> { session.UpstreamDestinationId };

        session.HttpClient.Response.StatusCode = 503;
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));
        seen.Add(session.UpstreamDestinationId);
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));
        seen.Add(session.UpstreamDestinationId);
        Assert.IsFalse(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));

        Assert.AreEqual(3, seen.Distinct().Count());
        Assert.AreEqual(2, session.Extras!.StatusRetries);
    }

    [TestMethod]
    public async Task OneAttempt_StopsAfterOneReplay()
    {
        var (proxy, _) = await StartAsync(new RetryConfig { Attempts = 1 });
        using var _p = proxy;
        var session = Session(proxy, "GET");
        ReverseProxySessionDispatch.Apply(proxy, session);
        session.HttpClient.Response.StatusCode = 502;
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));
        Assert.IsFalse(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));
    }

    [TestMethod]
    public async Task DefaultStatusList_IsBadGatewayUnavailableTimeout()
    {
        var (proxy, _) = await StartAsync(new RetryConfig { Attempts = 3 });
        using var _p = proxy;
        foreach (var (status, expected) in new[] { (500, false), (429, false), (404, false), (502, true), (503, true), (504, true) })
        {
            var session = Session(proxy, "GET");
            ReverseProxySessionDispatch.Apply(proxy, session);
            Assert.AreEqual(expected,
                ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session, status), status.ToString());
        }
    }

    [TestMethod]
    public async Task CustomStatusList_ReplacesTheDefaults()
    {
        var (proxy, _) = await StartAsync(new RetryConfig { Attempts = 2, StatusCodes = [429, 500] });
        using var _p = proxy;
        foreach (var (status, expected) in new[] { (429, true), (500, true), (503, false), (502, false) })
        {
            var session = Session(proxy, "GET");
            ReverseProxySessionDispatch.Apply(proxy, session);
            Assert.AreEqual(expected,
                ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session, status), status.ToString());
        }
    }

    [TestMethod]
    public async Task NonIdempotent_IsNeverRetried_WithoutTheFlag()
    {
        var (proxy, _) = await StartAsync(new RetryConfig { Attempts = 2 });
        using var _p = proxy;
        var session = Session(proxy, "POST");
        ReverseProxySessionDispatch.Apply(proxy, session);
        session.HttpClient.Response.StatusCode = 503;
        Assert.IsFalse(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));
        Assert.IsFalse(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session, bufferedBodyReplayable: true));
        Assert.AreEqual(0, session.Extras!.StatusRetries);

        var (flagged, _) = await StartAsync(new RetryConfig { Attempts = 2, RetryNonIdempotent = true });
        using var _f = flagged;
        var allowed = Session(flagged, "POST");
        ReverseProxySessionDispatch.Apply(flagged, allowed);
        allowed.HttpClient.Response.StatusCode = 503;
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(flagged, allowed));
    }

    [TestMethod]
    public async Task Body_ReplaysOnlyWhenBufferedWithinTheLimit_AndTheOriginCanSendItAgain()
    {
        var (proxy, _) = await StartAsync(new RetryConfig { Attempts = 2, RetryNonIdempotent = true, MaxBufferedBodyBytes = 8 });
        using var _p = proxy;

        SessionEventArgs Post(int size, bool buffered)
        {
            var session = Session(proxy, "POST");
            ReverseProxySessionDispatch.Apply(proxy, session);
            session.HttpClient.Response.StatusCode = 503;
            var request = session.HttpClient.Request;
            if (buffered)
            {
                request.Body = new byte[size];
                request.IsBodyRead = true;
            }
            else
            {
                request.ContentLength = size;
            }

            return session;
        }

        // A streamed body has already been consumed from the client.
        Assert.IsFalse(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, Post(4, false), bufferedBodyReplayable: true));
        // HTTP/1 origins resend headers only.
        Assert.IsFalse(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, Post(4, true)));
        // Too large for the buffered limit.
        Assert.IsFalse(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, Post(32, true), bufferedBodyReplayable: true));
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, Post(4, true), bufferedBodyReplayable: true));
    }

    [TestMethod]
    public void Backoff_DoublesPerReplay_AndIsCapped()
    {
        var retry = new RetryConfig { Attempts = 5, BackoffMilliseconds = 100, MaxBackoffMilliseconds = 350, JitterPercent = 0 };
        Assert.AreEqual(100, ReverseProxySessionDispatch.ComputeRetryDelayMilliseconds(retry, 0, null));
        Assert.AreEqual(200, ReverseProxySessionDispatch.ComputeRetryDelayMilliseconds(retry, 1, null));
        Assert.AreEqual(350, ReverseProxySessionDispatch.ComputeRetryDelayMilliseconds(retry, 2, null));
        Assert.AreEqual(350, ReverseProxySessionDispatch.ComputeRetryDelayMilliseconds(retry, 40, null));
        Assert.AreEqual(0, ReverseProxySessionDispatch.ComputeRetryDelayMilliseconds(new RetryConfig { Attempts = 1 }, 0, null));
    }

    [TestMethod]
    public void Backoff_JitterStaysWithinThePercent()
    {
        var retry = new RetryConfig { Attempts = 2, BackoffMilliseconds = 1000, MaxBackoffMilliseconds = 10_000, JitterPercent = 20 };
        for (var i = 0; i < 200; i++)
        {
            var delay = ReverseProxySessionDispatch.ComputeRetryDelayMilliseconds(retry, 0, null);
            Assert.IsTrue(delay is >= 1000 and <= 1200, delay.ToString());
        }
    }

    [TestMethod]
    public void RetryAfter_IsHonoredOnlyWhenAsked_AndCappedAtTheMaxBackoff()
    {
        var response = new Response { StatusCode = 503 };
        response.Headers.AddHeader("Retry-After", "30");

        var honor = new RetryConfig { Attempts = 1, HonorRetryAfter = true, MaxBackoffMilliseconds = 2000, BackoffMilliseconds = 50, JitterPercent = 0 };
        Assert.AreEqual(2000, ReverseProxySessionDispatch.ComputeRetryDelayMilliseconds(honor, 0, response));

        var small = new Response { StatusCode = 503 };
        small.Headers.AddHeader("Retry-After", "1");
        Assert.AreEqual(1000, ReverseProxySessionDispatch.ComputeRetryDelayMilliseconds(honor, 0, small));

        var ignore = new RetryConfig { Attempts = 1, HonorRetryAfter = false, MaxBackoffMilliseconds = 2000, BackoffMilliseconds = 50, JitterPercent = 0 };
        Assert.AreEqual(50, ReverseProxySessionDispatch.ComputeRetryDelayMilliseconds(ignore, 0, response));

        // An HTTP-date needs the origin's clock. Backoff applies instead.
        var date = new Response { StatusCode = 503 };
        date.Headers.AddHeader("Retry-After", "Wed, 21 Oct 2026 07:28:00 GMT");
        Assert.AreEqual(50, ReverseProxySessionDispatch.ComputeRetryDelayMilliseconds(honor, 0, date));
    }

    [TestMethod]
    public async Task Backoff_WaitsOnTheClusterTimeProvider_WithoutRealSleep()
    {
        var time = new FakeTimeProvider();
        var (proxy, _) = await StartAsync(
            new RetryConfig { Attempts = 2, BackoffMilliseconds = 700, MaxBackoffMilliseconds = 5000, JitterPercent = 0 }, time: time);
        using var _p = proxy;
        var session = Session(proxy, "GET");
        ReverseProxySessionDispatch.Apply(proxy, session);
        session.HttpClient.Response.StatusCode = 503;
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));

        var wait = ReverseProxySessionDispatch.WaitBeforeRetryAsync(proxy, session, CancellationToken.None).AsTask();
        Assert.IsFalse(wait.IsCompleted);
        Assert.AreEqual(1, time.Timers.Count);
        Assert.AreEqual(TimeSpan.FromMilliseconds(700), time.Timers[0].DueTime);

        time.Timers[0].Fire();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        // The delay is spent once. The next replay chooses its own.
        Assert.IsTrue(ReverseProxySessionDispatch.WaitBeforeRetryAsync(proxy, session, CancellationToken.None).IsCompletedSuccessfully);
        session.HttpClient.Response.StatusCode = 503;
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));
        _ = ReverseProxySessionDispatch.WaitBeforeRetryAsync(proxy, session, CancellationToken.None).AsTask();
        Assert.AreEqual(TimeSpan.FromMilliseconds(1400), time.Timers[1].DueTime);
    }

    [TestMethod]
    public async Task Backoff_ACancelledToken_AbortsTheWait()
    {
        var time = new FakeTimeProvider();
        var (proxy, _) = await StartAsync(
            new RetryConfig { Attempts = 1, BackoffMilliseconds = 700, JitterPercent = 0 }, time: time);
        using var _p = proxy;
        var session = Session(proxy, "GET");
        ReverseProxySessionDispatch.Apply(proxy, session);
        session.HttpClient.Response.StatusCode = 503;
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));

        using var cts = new CancellationTokenSource();
        var wait = ReverseProxySessionDispatch.WaitBeforeRetryAsync(proxy, session, cts.Token).AsTask();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task Budget_RefusesAReplayWhileTheSlotIsHeld_AndCountsTheRefusal()
    {
        long refused = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Titanium.Web.Proxy" && instrument.Name == "twp.proxy.status.retries_refused")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason" && (tag.Value as string) == "budget")
                {
                    Interlocked.Add(ref refused, value);
                }
            }
        });
        listener.Start();

        var (proxy, manager) = await StartAsync(
            new RetryConfig { Attempts = 2, BudgetPercent = 10, BudgetMinConcurrent = 1 });
        using var _p = proxy;
        var runtime = ClusterRuntime.Find(manager.Snapshot, "c")!;

        var first = Session(proxy, "GET");
        ReverseProxySessionDispatch.Apply(proxy, first);
        first.HttpClient.Response.StatusCode = 503;
        var second = Session(proxy, "GET");
        ReverseProxySessionDispatch.Apply(proxy, second);
        second.HttpClient.Response.StatusCode = 503;

        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, first));
        Assert.AreEqual(1, runtime.ActiveRetries);
        Assert.IsFalse(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, second));
        Assert.AreEqual(1, runtime.ActiveRetries);
        Assert.AreEqual(0, second.Extras!.StatusRetries);
        Assert.IsTrue(Interlocked.Read(ref refused) >= 1);

        ReverseProxySessionDispatch.EndGatewayRetry(first);
        Assert.AreEqual(0, runtime.ActiveRetries);
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, second));
        ReverseProxySessionDispatch.EndGatewayRetry(second);
        Assert.AreEqual(0, runtime.ActiveRetries);
    }

    [TestMethod]
    public async Task Budget_SlotHeldAcrossAttempts_IsReleasedOnceAndByReset()
    {
        var (proxy, manager) = await StartAsync(
            new RetryConfig { Attempts = 2, BudgetPercent = 50, BudgetMinConcurrent = 1 });
        using var _p = proxy;
        var runtime = ClusterRuntime.Find(manager.Snapshot, "c")!;
        var session = Session(proxy, "GET");
        ReverseProxySessionDispatch.Apply(proxy, session);
        session.HttpClient.Response.StatusCode = 503;

        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));
        Assert.AreEqual(1, runtime.ActiveRetries, "one request holds one slot, however many replays it makes");

        // A keep-alive connection starts the next request clean.
        session.Extras!.Reset();
        Assert.AreEqual(0, runtime.ActiveRetries);
        Assert.AreEqual(0, session.Extras.StatusRetries);
        Assert.IsFalse(session.Extras.PerTryTimeoutActive);
    }

    [TestMethod]
    public async Task PerTryTimeout_NeedsAnotherDestination_AndCountsAsA504()
    {
        var (proxy, _) = await StartAsync(new RetryConfig { Attempts = 1, PerTryTimeoutMilliseconds = 300 });
        using var _p = proxy;
        var session = Session(proxy, "GET");
        ReverseProxySessionDispatch.Apply(proxy, session);
        Assert.IsTrue(session.Extras!.PerTryTimeoutActive);
        Assert.AreEqual(TimeSpan.FromMilliseconds(300), session.ResponseHeaderTimeout);

        // Not a status: the timeout path stages it.
        Assert.IsFalse(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session, 200));
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session, 504, timedOut: true));

        // The last attempt goes back to the route's own deadline.
        Assert.IsFalse(session.Extras.PerTryTimeoutActive);
        Assert.IsNull(session.ResponseHeaderTimeout);

        var (single, _) = await StartAsync(new RetryConfig { Attempts = 1, PerTryTimeoutMilliseconds = 300 }, destinations: 1);
        using var _s = single;
        var alone = Session(single, "GET");
        ReverseProxySessionDispatch.Apply(single, alone);
        Assert.IsFalse(alone.Extras!.PerTryTimeoutActive);
        Assert.IsNull(alone.ResponseHeaderTimeout);
        Assert.IsFalse(ReverseProxySessionDispatch.TryStageGatewayRetry(single, alone, 504, timedOut: true));
    }

    [TestMethod]
    public async Task PerTryTimeout_DoesNotLoosenAStricterRouteDeadline()
    {
        var (proxy, _) = await StartAsync(new RetryConfig { Attempts = 1, PerTryTimeoutMilliseconds = 5000 });
        using var _p = proxy;
        var session = Session(proxy, "GET");
        session.ResponseHeaderTimeout = TimeSpan.FromMilliseconds(100);
        ReverseProxySessionDispatch.Apply(proxy, session);
        Assert.IsFalse(session.Extras!.PerTryTimeoutActive);
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), session.ResponseHeaderTimeout);
    }

    [TestMethod]
    public async Task KeepAliveReuse_StartsEachRequestWithAFreshRetryState()
    {
        var (proxy, _) = await StartAsync(new RetryConfig { Attempts = 1 });
        using var _p = proxy;
        var session = Session(proxy, "GET");
        ReverseProxySessionDispatch.Apply(proxy, session);
        session.HttpClient.Response.StatusCode = 503;
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));
        Assert.IsFalse(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));

        session.Extras!.Reset();
        ReverseProxySessionDispatch.Apply(proxy, session);
        session.HttpClient.Response.StatusCode = 503;
        Assert.AreEqual(0, session.Extras!.StatusRetries);
        Assert.IsTrue(ReverseProxySessionDispatch.TryStageGatewayRetry(proxy, session));
    }

    private static async Task<(ProxyServer Proxy, ClusterManager Manager)> StartAsync(
        RetryConfig retry, int destinations = 3, TimeProvider? time = null)
    {
        var proxy = new ProxyServer(userTrustRootCertificate: false);
        var manager = time is null ? new ClusterManager() : new ClusterManager(time);
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations = Enumerable.Range(0, destinations)
                    .Select(i => new DestinationConfig { Id = "d" + i, Address = "10.0.0." + (i + 1) })
                    .ToList(),
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
                    Retry = retry,
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        return (proxy, manager);
    }

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

    private sealed class FakeTimeProvider : TimeProvider
    {
        public List<FakeTimer> Timers { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(callback, state, dueTime);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly TimerCallback callback;
        private readonly object? state;

        public FakeTimer(TimerCallback callback, object? state, TimeSpan dueTime)
        {
            this.callback = callback;
            this.state = state;
            DueTime = dueTime;
        }

        public TimeSpan DueTime { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return true;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
