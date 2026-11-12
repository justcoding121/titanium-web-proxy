using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Caching;

namespace Titanium.Plus.Tests.Caching;

/// <summary>
///     Unit coverage for <see cref="RequestCollapser"/>: Leader / Waiter / Independent role assignment,
///     flight accounting, waiter-cap, flight-cap, TryLead, WaitAsync timeout and cancel branches,
///     and Complete idempotency.  Bug class: cache stampede — many concurrent misses for the same
///     key result in many redundant origin fetches instead of one.
/// </summary>
[TestClass]
public class RequestCollapserTests
{
    // ---------------------------------------------------------------- Enter — role assignment

    [TestMethod]
    public void Enter_ReturnsLeader_WhenNoFlightExists()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 4);
        var role = c.Enter("key1", out var flight);

        Assert.AreEqual(RequestCollapser.Role.Leader, role);
        Assert.IsNotNull(flight);
        Assert.AreEqual("key1", flight.Key);
        Assert.AreEqual(1, c.InFlight);
    }

    [TestMethod]
    public void Enter_ReturnsWaiter_WhenLeaderAlreadyInFlight()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 4);
        c.Enter("key1", out var leader);

        var role = c.Enter("key1", out var waiter);
        Assert.AreEqual(RequestCollapser.Role.Waiter, role);
        Assert.IsNotNull(waiter);
        Assert.AreSame(leader, waiter);
        Assert.AreEqual(1, c.InFlight);
    }

    [TestMethod]
    public void Enter_ReturnsLeader_ForDifferentKey()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 4);
        c.Enter("key1", out _);
        var role = c.Enter("key2", out var flight2);

        Assert.AreEqual(RequestCollapser.Role.Leader, role);
        Assert.IsNotNull(flight2);
        Assert.AreEqual(2, c.InFlight);
    }

    [TestMethod]
    public void Enter_ReturnsIndependent_WhenWaiterCapReached()
    {
        var c = new RequestCollapser(maxWaiters: 2, maxFlights: 4);
        c.Enter("key1", out _);           // leader
        c.Enter("key1", out _);           // waiter 1
        c.Enter("key1", out _);           // waiter 2 (cap = 2)
        var role = c.Enter("key1", out var flight);

        Assert.AreEqual(RequestCollapser.Role.Independent, role);
        Assert.IsNull(flight);
    }

    [TestMethod]
    public void Enter_ReturnsIndependent_WhenFlightCapReached()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 2);
        c.Enter("k1", out _);    // leader for k1
        c.Enter("k2", out _);    // leader for k2 — at cap
        var role = c.Enter("k3", out var flight);

        Assert.AreEqual(RequestCollapser.Role.Independent, role);
        Assert.IsNull(flight);
    }

    [TestMethod]
    public void Enter_Zero_MaxWaiters_AlwaysIndependentAfterLeader()
    {
        var c = new RequestCollapser(maxWaiters: 0, maxFlights: 4);
        c.Enter("k", out _);  // leader
        var role = c.Enter("k", out _);

        Assert.AreEqual(RequestCollapser.Role.Independent, role);
    }

    // ---------------------------------------------------------------- Complete

    [TestMethod]
    public void Complete_ReleasesWaiters_AndDecrementsInFlight()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 4);
        c.Enter("k", out var leader);
        c.Enter("k", out _);  // waiter

        Assert.AreEqual(1, c.InFlight);
        c.Complete(leader!);
        Assert.AreEqual(0, c.InFlight);
        Assert.IsTrue(leader!.Completion.IsCompleted);
    }

    [TestMethod]
    public void Complete_IsIdempotent_CalledTwice()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 4);
        c.Enter("k", out var leader);
        c.Complete(leader!);
        // Should not throw.
        c.Complete(leader!);
        Assert.AreEqual(0, c.InFlight);
    }

    [TestMethod]
    public void Complete_AllowsNewLeaderForSameKey()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 4);
        c.Enter("k", out var first);
        c.Complete(first!);

        var role = c.Enter("k", out var second);
        Assert.AreEqual(RequestCollapser.Role.Leader, role);
        Assert.IsNotNull(second);
        Assert.AreEqual(1, c.InFlight);
    }

    // ---------------------------------------------------------------- TryLead

    [TestMethod]
    public void TryLead_ReturnsTrue_WhenNoFlightExists()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 4);
        var ok = c.TryLead("k", out var flight);

        Assert.IsTrue(ok);
        Assert.IsNotNull(flight);
        Assert.AreEqual(1, c.InFlight);
    }

    [TestMethod]
    public void TryLead_ReturnsFalse_WhenFlightAlreadyExists()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 4);
        c.TryLead("k", out _);  // first lead

        var ok = c.TryLead("k", out var flight);
        Assert.IsFalse(ok);
        Assert.IsNull(flight);
        Assert.AreEqual(1, c.InFlight);
    }

    [TestMethod]
    public void TryLead_ReturnsFalse_WhenAtFlightCap()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 1);
        c.TryLead("k1", out _);  // fills cap

        var ok = c.TryLead("k2", out var flight);
        Assert.IsFalse(ok);
        Assert.IsNull(flight);
    }

    // ---------------------------------------------------------------- WaitAsync

    [TestMethod]
    [Timeout(10_000)]
    public async Task WaitAsync_ReturnsTrue_WhenLeaderCompletesBeforeTimeout()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 4);
        c.Enter("k", out var leader);
        c.Enter("k", out var waiter);

        var waitTask = RequestCollapser.WaitAsync(waiter!, timeout: TimeSpan.FromSeconds(5), CancellationToken.None);
        c.Complete(leader!);
        var result = await waitTask;

        Assert.IsTrue(result);
        Assert.AreEqual(0, waiter!.Waiters);  // RemoveWaiter was called in finally
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task WaitAsync_ReturnsFalse_WhenTimeout()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 4);
        c.Enter("k", out var leader);
        c.Enter("k", out var waiter);

        // The leader never completes → WaitAsync times out.
        var result = await RequestCollapser.WaitAsync(waiter!, timeout: TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.IsFalse(result);

        // Cleanup so the test doesn't leak.
        c.Complete(leader!);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task WaitAsync_ThrowsOperationCanceled_WhenCallerTokenCancelled()
    {
        // The filter `when (!cancellationToken.IsCancellationRequested)` lets the caller's
        // own cancellation propagate; only a timeout-driven cancel is mapped to false.
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 4);
        c.Enter("k", out var leader);
        c.Enter("k", out var waiter);

        using var cts = new CancellationTokenSource();
        var waitTask = RequestCollapser.WaitAsync(waiter!, timeout: TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<System.Threading.Tasks.TaskCanceledException>(async () => await waitTask);

        // Cleanup.
        c.Complete(leader!);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task WaitAsync_AlreadyCompleted_ReturnsTrue_Synchronously()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 4);
        c.Enter("k", out var leader);
        c.Enter("k", out var waiter);
        c.Complete(leader!);

        // Completion is already set; WaitAsync should not need to wait.
        var result = await RequestCollapser.WaitAsync(waiter!, timeout: TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.IsTrue(result);
    }

    // ---------------------------------------------------------------- InFlight tracking

    [TestMethod]
    public void InFlight_ReflectsOpenFlights()
    {
        var c = new RequestCollapser(maxWaiters: 4, maxFlights: 10);
        Assert.AreEqual(0, c.InFlight);

        c.Enter("a", out var a);
        c.Enter("b", out var b);
        c.Enter("c", out var c3);
        Assert.AreEqual(3, c.InFlight);

        c.Complete(a!);
        Assert.AreEqual(2, c.InFlight);
        c.Complete(b!);
        c.Complete(c3!);
        Assert.AreEqual(0, c.InFlight);
    }

    // ---------------------------------------------------------------- Flight internals

    [TestMethod]
    public void Flight_WaitersCount_TracksAddAndRemove()
    {
        var flight = new RequestCollapser.Flight("x");
        Assert.AreEqual(0, flight.Waiters);

        flight.TryAddWaiter(2);
        Assert.AreEqual(1, flight.Waiters);

        flight.TryAddWaiter(2);
        Assert.AreEqual(2, flight.Waiters);

        // Cap reached; next add is refused.
        var ok = flight.TryAddWaiter(2);
        Assert.IsFalse(ok);
        Assert.AreEqual(2, flight.Waiters);  // didn't increment on failure

        flight.RemoveWaiter();
        Assert.AreEqual(1, flight.Waiters);
    }

    [TestMethod]
    public void Flight_Release_IsIdempotent()
    {
        var flight = new RequestCollapser.Flight("y");
        flight.Release();
        flight.Release();  // must not throw

        Assert.IsTrue(flight.Completion.IsCompleted);
    }
}
