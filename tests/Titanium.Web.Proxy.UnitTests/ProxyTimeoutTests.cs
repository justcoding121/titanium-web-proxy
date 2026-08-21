using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ProxyTimeoutTests
{
    [TestMethod]
    public void ProxyServer_Timeout_Defaults_Are_Disabled_And_Legacy_Timeouts_Unchanged()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.AreEqual(0, proxy.ResponseHeaderTimeoutSeconds);
        Assert.AreEqual(0, proxy.IdleReadTimeoutSeconds);
        Assert.AreEqual(0, proxy.IdleWriteTimeoutSeconds);
        Assert.AreEqual(0, proxy.RequestTimeoutSeconds);
        Assert.AreEqual(0, proxy.ClientHeaderTimeoutSeconds);
        Assert.AreEqual(60, proxy.ConnectionTimeOutSeconds);
        Assert.AreEqual(20, proxy.ConnectTimeOutSeconds);
    }

    [TestMethod]
    public async Task Deadline_CancelAfter_Raises_Typed_Timeout_From_Adjacent_Catch()
    {
        using var parent = new CancellationTokenSource();
        var registry = new DeadlineRegistry();
        using var deadline = registry.Start(parent.Token, TimeSpan.FromMilliseconds(50),
            ProxyTimeoutKind.ResponseHeader);

        try
        {
            await Task.Delay(Timeout.Infinite, deadline.Token);
            Assert.Fail("Expected cancellation");
        }
        catch (OperationCanceledException ex)
        {
            try
            {
                // Called on the still-live Deadline, exactly as every real catch site does - before
                // its `using` has disposed it - which is the case ThrowIfTimedOut must get right.
                deadline.ThrowIfTimedOut(ex);
                Assert.Fail("Expected ProxyTimeoutException");
            }
            catch (ProxyTimeoutException timeout)
            {
                Assert.AreEqual(ProxyTimeoutKind.ResponseHeader, timeout.Kind);
            }
        }
    }

    [TestMethod]
    public void Deadline_Parent_Cancel_Does_Not_Report_Timeout()
    {
        using var parent = new CancellationTokenSource();
        var registry = new DeadlineRegistry();
        using var deadline = registry.Start(parent.Token, TimeSpan.FromSeconds(30), ProxyTimeoutKind.Request);

        parent.Cancel();
        Assert.IsFalse(registry.TryGetFiredKind(out _));

        try
        {
            deadline.ThrowIfTimedOut(new OperationCanceledException(deadline.Token));
            Assert.Fail("Expected OperationCanceledException");
        }
        catch (ProxyTimeoutException)
        {
            Assert.Fail("Parent cancellation must not be reported as ProxyTimeoutException");
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    [TestMethod]
    public void Deadline_Without_Deadline_Forwards_Parent_Token()
    {
        using var parent = new CancellationTokenSource();
        var registry = new DeadlineRegistry();
        using var deadline = registry.Start(parent.Token, null, ProxyTimeoutKind.IdleRead);

        Assert.IsFalse(deadline.HasDeadline);
        Assert.AreEqual(parent.Token, deadline.Token);
    }

    [TestMethod]
    public async Task DeadlineRegistry_Attributes_Innermost_Deadline_After_Inner_Scope_Unwinds_Without_Its_Own_Catch()
    {
        // Reproduces the scenario ProxyTimeoutScope could not handle: an inner deadline fires and its
        // `using` block unwinds (disposing the inner scope, with no catch of its own) before an outer,
        // un-nested catch several layers up inspects anything. The registry must still know which one
        // fired, recorded by Deadline.Dispose() as the inner scope unwound.
        using var parent = new CancellationTokenSource();
        var registry = new DeadlineRegistry();

        Exception? caught = null;
        using (var outer = registry.Start(parent.Token, TimeSpan.FromSeconds(30), ProxyTimeoutKind.Request))
        {
            try
            {
                using var inner = registry.Start(outer.Token, TimeSpan.FromMilliseconds(30),
                    ProxyTimeoutKind.IdleWrite);
                // No catch here: the inner scope's `using` disposes it (and records the firing) as
                // part of unwinding past this frame, before the outer catch below ever runs.
                await Task.Delay(Timeout.Infinite, inner.Token);
            }
            catch (OperationCanceledException ex)
            {
                caught = ex;
            }
        }

        Assert.IsNotNull(caught);
        Assert.IsTrue(registry.TryGetFiredKind(out var kind));
        Assert.AreEqual(ProxyTimeoutKind.IdleWrite, kind);
    }

    [TestMethod]
    public async Task Deadline_TryGetTimeoutException_Is_Not_Racy_Against_A_Synchronous_Downstream_Continuation()
    {
        // Regression test for a real bug: recording a firing via a CancellationToken.Register callback
        // (rather than a synchronous check inside TryGetTimeoutException/Dispose) can lose the firing
        // entirely. CancellationTokenSource.Cancel invokes every registered callback for a token in LIFO
        // order; if a callback registered *after* ours (e.g. deep inside a stream's own cancellation
        // shim) synchronously resumes its awaiter inline, that awaiter can run all the way through this
        // catch and this deadline's disposal - unregistering our not-yet-invoked callback - before
        // Cancel's loop ever reaches it. Simulating exactly that: a second, later registration on the
        // same token whose callback synchronously drives the catch-and-dispose sequence before returning.
        using var parent = new CancellationTokenSource();
        var registry = new DeadlineRegistry();
        using var deadline = registry.Start(parent.Token, TimeSpan.FromMilliseconds(50), ProxyTimeoutKind.IdleRead);

        ProxyTimeoutException? observed = null;
        using (deadline.Token.Register(() =>
               {
                   // Registered after the Deadline's own construction, so - matching real
                   // CancellationTokenSource LIFO callback ordering - this runs first when the token
                   // is cancelled, exactly mirroring the real bug's timing.
                   try
                   {
                       throw new OperationCanceledException(deadline.Token);
                   }
                   catch (OperationCanceledException ex)
                   {
                       deadline.TryGetTimeoutException(ex, out observed);
                   }
               }))
        {
            try
            {
                await Task.Delay(Timeout.Infinite, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                // expected; the inline callback above already ran synchronously as part of Cancel().
            }
        }

        Assert.IsNotNull(observed, "The firing must be attributed even though a later-registered " +
                                    "callback ran first and would have raced a Register-callback-based design.");
        Assert.AreEqual(ProxyTimeoutKind.IdleRead, observed!.Kind);
    }

    [TestMethod]
    public void Deadline_ThrowIfTimedOut_Preserves_Original_Stack_Trace_When_Not_Timed_Out()
    {
        using var parent = new CancellationTokenSource();
        var registry = new DeadlineRegistry();
        using var deadline = registry.Start(parent.Token, TimeSpan.FromSeconds(30), ProxyTimeoutKind.Request);

        Exception original;
        try
        {
            ThrowHelper();
            throw new InvalidOperationException("unreachable");
        }
        catch (OperationCanceledException ex)
        {
            original = ex;
        }

        try
        {
            deadline.ThrowIfTimedOut(original);
            Assert.Fail("Expected OperationCanceledException to be rethrown");
        }
        catch (OperationCanceledException ex)
        {
            StringAssert.Contains(ex.StackTrace, nameof(ThrowHelper));
        }
    }

    [TestMethod]
    public void DeadlineRegistry_Reuses_Passthrough_Scopes_When_Timeout_Disabled()
    {
        using var parent = new CancellationTokenSource();
        var registry = new DeadlineRegistry();

        DeadlineRegistry.Deadline first;
        DeadlineRegistry.Deadline nested;
        using (first = registry.Start(parent.Token, null, ProxyTimeoutKind.ClientHeader))
        using (nested = registry.Start(parent.Token, TimeSpan.Zero, ProxyTimeoutKind.Request))
        {
            Assert.AreEqual(parent.Token, first.Token);
            Assert.AreEqual(parent.Token, nested.Token);
            Assert.AreNotSame(first, nested);
        }

        using var again = registry.Start(parent.Token, null, ProxyTimeoutKind.IdleWrite);
        Assert.AreSame(first, again);
        Assert.AreEqual(parent.Token, again.Token);
    }

    private static void ThrowHelper()
    {
        throw new OperationCanceledException();
    }
}
