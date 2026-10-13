using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class LinkShaperTests
{
    [TestMethod]
    public void ManySmallReservations_EndAtTheSameClock_AsOneLarge()
    {
        var time = new ManualTimeProvider();
        var profile = new NetworkThrottleProfile("t", latencyMs: 0, bytesPerSecond: 1_000);
        using var one = new LinkShaper(profile, time, NoSleep);
        using var many = new LinkShaper(profile, time, NoSleep);

        var single = one.Reserve(ThrottleDirection.Download, 1_000);
        TimeSpan last = TimeSpan.Zero;
        for (var i = 0; i < 5; i++)
        {
            last = many.Reserve(ThrottleDirection.Download, 200);
        }

        Assert.AreEqual(single, last);
        Assert.AreEqual(TimeSpan.FromSeconds(1), single);
    }

    [TestMethod]
    public void SecondReservation_WaitsBehindTheFirst()
    {
        var time = new ManualTimeProvider();
        using var shaper = new LinkShaper(
            new NetworkThrottleProfile("t", 0, 1_000), time, NoSleep);

        var first = shaper.Reserve(ThrottleDirection.Download, 500);
        var second = shaper.Reserve(ThrottleDirection.Download, 500);

        Assert.AreEqual(TimeSpan.FromMilliseconds(500), first);
        Assert.AreEqual(TimeSpan.FromSeconds(1), second);
    }

    [TestMethod]
    public void IdleGap_DoesNotBankCredit()
    {
        var time = new ManualTimeProvider();
        using var shaper = new LinkShaper(
            new NetworkThrottleProfile("t", 0, 1_000), time, NoSleep);

        Assert.AreEqual(TimeSpan.FromSeconds(1), shaper.Reserve(ThrottleDirection.Upload, 1_000));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.AreEqual(TimeSpan.FromSeconds(1), shaper.Reserve(ThrottleDirection.Upload, 1_000));
    }

    [TestMethod]
    public void ZeroBytes_DoNotMoveTheClock()
    {
        var time = new ManualTimeProvider();
        using var shaper = new LinkShaper(
            new NetworkThrottleProfile("t", 0, 1_000), time, NoSleep);

        Assert.AreEqual(TimeSpan.FromMilliseconds(100), shaper.Reserve(ThrottleDirection.Download, 100));
        Assert.AreEqual(TimeSpan.Zero, shaper.Reserve(ThrottleDirection.Download, 0));
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), shaper.Reserve(ThrottleDirection.Download, 100));
    }

    [TestMethod]
    public void Latency_DoesNotMoveTheBandwidthClock()
    {
        var time = new ManualTimeProvider();
        using var shaper = new LinkShaper(NetworkThrottle.Slow3G, time, NoSleep);
        Assert.AreEqual(TimeSpan.FromMilliseconds(400), shaper.LatencyDelay);
        var before = shaper.Reserve(ThrottleDirection.Download, 50_000);
        _ = shaper.LatencyDelay;
        time.Advance(before);
        var after = shaper.Reserve(ThrottleDirection.Download, 50_000);
        Assert.AreEqual(before, after);
    }

    [TestMethod]
    public async Task ReleaseWaiters_UnblocksWithoutThrowing()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shaper = new LinkShaper(
            NetworkThrottle.Slow3G,
            delay: (span, token) =>
            {
                started.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            });
        var blocked = shaper.WaitAsync(TimeSpan.FromMinutes(10));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        shaper.ReleaseWaiters();
        await blocked.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task CallerCancel_StillThrows()
    {
        using var shaper = new LinkShaper(NetworkThrottle.LTE, delay: BlockUntilCancelled);
        using var cts = new CancellationTokenSource();
        var waiting = shaper.WaitAsync(TimeSpan.FromMinutes(10), cts.Token);
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await waiting);
    }

    [TestMethod]
    public async Task Wait_IsCappedAtTenMinutes()
    {
        TimeSpan? seen = null;
        using var shaper = new LinkShaper(
            new NetworkThrottleProfile("t", 0, 1),
            delay: (span, token) =>
            {
                seen = span;
                return Task.CompletedTask;
            });
        await shaper.WaitAsync(TimeSpan.FromHours(2));
        Assert.AreEqual(LinkShaper.MaxWait, seen);
    }

    private static Task NoSleep(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;

    private static Task BlockUntilCancelled(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by) => _timestamp += by.Ticks;

        public override long GetTimestamp() => _timestamp;
    }
}
