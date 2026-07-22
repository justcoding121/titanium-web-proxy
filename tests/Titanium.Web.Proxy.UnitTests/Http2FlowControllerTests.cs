#if NET6_0_OR_GREATER
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for <see cref="Http2FlowController" />, the SEND-side flow-control accounting used by
///     <c>Http2Helper</c> for every outbound DATA frame on one leg of the HTTP/2 relay (RFC 7540 §6.9).
/// </summary>
[TestClass]
public class Http2FlowControllerTests
{
    [TestMethod]
    public async Task ReserveAsync_WithinWindow_ReturnsImmediatelyAndDecrementsBothWindows()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);

        await flow.ReserveAsync(1, 1000, CancellationToken.None);

        // a second reservation up to the remaining window must still succeed without blocking.
        var task = flow.ReserveAsync(1, Http2FlowController.InitialConnectionWindow - 1000, CancellationToken.None);
        Assert.IsTrue(task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task ReserveAsync_ExceedingStreamWindow_BlocksUntilWindowUpdate()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);

        // give the connection window plenty of extra headroom so only stream 1's own window is the
        // constraint under test below.
        flow.OnWindowUpdate(0, 1_000_000);

        // exhaust stream 1's window entirely while the connection window still has ample credit.
        await flow.ReserveAsync(1, Http2FlowController.InitialConnectionWindow, CancellationToken.None);

        var reserveTask = flow.ReserveAsync(1, 10, CancellationToken.None);
        await Task.Delay(50);
        Assert.IsFalse(reserveTask.IsCompleted, "Reservation should block while the stream window is exhausted.");

        flow.OnWindowUpdate(1, 20);

        await reserveTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(reserveTask.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task ReserveAsync_ExceedingConnectionWindow_BlocksUntilConnectionWindowUpdate()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        flow.RegisterStream(2);

        // drain the connection window using stream 1, leaving stream 2's window untouched.
        await flow.ReserveAsync(1, Http2FlowController.InitialConnectionWindow, CancellationToken.None);

        var reserveTask = flow.ReserveAsync(2, 10, CancellationToken.None);
        await Task.Delay(50);
        Assert.IsFalse(reserveTask.IsCompleted, "Reservation should block while the connection window is exhausted even though the stream window is untouched.");

        flow.OnWindowUpdate(0, 20);

        await reserveTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(reserveTask.IsCompletedSuccessfully);
    }

    [TestMethod]
    public void OnWindowUpdate_ZeroForStream_IsIgnoredWithoutThrowing()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);

        var overflow = flow.OnWindowUpdate(1, 0);

        Assert.IsFalse(overflow);
    }

    [TestMethod]
    public void OnWindowUpdate_ForUntrackedStream_IsIgnored()
    {
        var flow = new Http2FlowController();

        // stream 99 was never registered (e.g. already removed by RST_STREAM) - must not throw.
        var overflow = flow.OnWindowUpdate(99, 100);

        Assert.IsFalse(overflow);
    }

    [TestMethod]
    public void OnWindowUpdate_DrivingConnectionWindowAboveMax_ReportsOverflow()
    {
        var flow = new Http2FlowController();

        var overflow = flow.OnWindowUpdate(0, int.MaxValue);

        Assert.IsTrue(overflow, "A WINDOW_UPDATE that would push the connection window above 2^31-1 must be reported as an overflow (FLOW_CONTROL_ERROR).");
    }

    [TestMethod]
    public void OnWindowUpdate_DrivingStreamWindowAboveMax_ReportsOverflow()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);

        var overflow = flow.OnWindowUpdate(1, int.MaxValue);

        Assert.IsTrue(overflow, "A WINDOW_UPDATE that would push a stream window above 2^31-1 must be reported as an overflow (FLOW_CONTROL_ERROR).");
    }

    [TestMethod]
    public async Task OnInitialWindowSizeChanged_Decrease_CanDriveOpenStreamWindowNegative_AndReserveStillBlocks()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);

        // shrink the initial window well below what's already been implicitly "granted" to stream 1.
        flow.OnInitialWindowSizeChanged(0);

        var reserveTask = flow.ReserveAsync(1, 1, CancellationToken.None);
        await Task.Delay(50);
        Assert.IsFalse(reserveTask.IsCompleted, "A stream window driven to zero/negative by SETTINGS_INITIAL_WINDOW_SIZE must still block new reservations (RFC 7540 §6.9.2).");

        flow.OnWindowUpdate(1, 10);
        await reserveTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(reserveTask.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task OnInitialWindowSizeChanged_Increase_GrantsExistingStreamsMoreCredit()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);

        // consume the entire initial window for stream 1.
        await flow.ReserveAsync(1, Http2FlowController.InitialConnectionWindow, CancellationToken.None);

        // widen the connection window too so only the stream-level increase is under test.
        flow.OnWindowUpdate(0, 100);
        flow.OnInitialWindowSizeChanged(Http2FlowController.InitialConnectionWindow + 100);

        // the +100 delta should now be available on stream 1 without any further WINDOW_UPDATE for it.
        await flow.ReserveAsync(1, 100, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void RegisteringSameStreamTwice_DoesNotThrow_AndResetsItsWindow()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        flow.RegisterStream(1);
    }

    [TestMethod]
    public void RemoveStream_ForUnknownStream_DoesNotThrow()
    {
        var flow = new Http2FlowController();
        flow.RemoveStream(12345);
    }

    [TestMethod]
    public async Task ReserveAsync_CancellationToken_CancelsPendingWaiterWithoutAffectingOthers()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        flow.RegisterStream(2);

        await flow.ReserveAsync(1, Http2FlowController.InitialConnectionWindow, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var cancelledTask = flow.ReserveAsync(1, 10, cts.Token);

        cts.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await cancelledTask);

        // stream 2, whose window was untouched, must be unaffected by the other waiter's cancellation once
        // the connection window is replenished.
        flow.OnWindowUpdate(0, 10);
        await flow.ReserveAsync(2, 10, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ReserveAsync_MultipleConcurrentWaiters_AllEventuallyComplete()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);

        // exhaust the window first.
        await flow.ReserveAsync(1, Http2FlowController.InitialConnectionWindow, CancellationToken.None);

        var waiters = new Task[5];
        for (var i = 0; i < waiters.Length; i++)
        {
            waiters[i] = flow.ReserveAsync(1, 100, CancellationToken.None);
        }

        foreach (var w in waiters)
        {
            Assert.IsFalse(w.IsCompleted);
        }

        // grant just enough credit, in several increments, on both the connection and the stream window,
        // for all waiters to eventually drain - each waiter re-checks the window after every wake-up rather
        // than assuming it will get served in order.
        for (var i = 0; i < waiters.Length; i++)
        {
            flow.OnWindowUpdate(0, 100);
            flow.OnWindowUpdate(1, 100);
        }

        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ReserveAsync_ZeroOrNegativeBytes_IsNoOp()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);

        // must return immediately without consuming any window or requiring registration.
        await flow.ReserveAsync(1, 0, CancellationToken.None);
        await flow.ReserveAsync(42, 0, CancellationToken.None);
    }

    [TestMethod]
    public async Task ReserveAsync_UnregisteredStream_UsesCurrentInitialWindowDefensively()
    {
        var flow = new Http2FlowController();

        // stream 7 was never explicitly registered - ReserveAsync must not throw, treating it as having
        // the controller's current initial window rather than failing the write outright.
        await flow.ReserveAsync(7, 100, CancellationToken.None);
    }
}
#endif
