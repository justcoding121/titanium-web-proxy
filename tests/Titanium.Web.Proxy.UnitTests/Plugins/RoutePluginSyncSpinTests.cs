using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Plugins;

namespace Titanium.Web.Proxy.UnitTests.Plugins;

/// <summary>
/// Documents a known limit of the plugin wall: it is cooperative, so a plugin that spins before its first await
/// holds the request thread and the timeout cannot preempt it. These tests pin that behaviour so a change is deliberate.
/// </summary>
[TestClass]
public class RoutePluginSyncSpinTests
{
    private sealed class SpinThenContinue(TimeSpan spin) : IProxyMiddleware
    {
        public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken)
        {
            // Synchronous spin: nothing is awaited and the token is never looked at.
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < spin)
            {
                Thread.SpinWait(100);
            }

            return next(context, cancellationToken);
        }
    }

    private sealed class AwaitForever : IProxyMiddleware
    {
        public async ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(ProxyMiddlewareContext Context, bool NextCalled, TimeSpan Elapsed)> RunAsync(
        IProxyMiddleware plugin, bool failOpen, TimeSpan timeout)
    {
        using var host = PluginHost.Load([]);
        var wall = new RoutePluginMiddleware("spin", plugin, failOpen, timeout, host, logger: null);
        var context = new ProxyMiddlewareContext { Session = new object() };
        var nextCalled = false;
        var clock = Stopwatch.StartNew();

        // On its own thread, so a plugin that never yields blocks only that thread and the test can still measure it.
        await Task.Run(async () => await wall.InvokeAsync(
            context,
            (_, _) =>
            {
                nextCalled = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None));
        return (context, nextCalled, clock.Elapsed);
    }

    [TestMethod]
    public async Task SyncSpin_IsNotPreemptedByTheTimeout_AndFailModeClosedDoesNotApply()
    {
        var timeout = TimeSpan.FromMilliseconds(30);
        var spin = TimeSpan.FromMilliseconds(400);

        var (context, nextCalled, elapsed) = await RunAsync(new SpinThenContinue(spin), failOpen: false, timeout);

        Assert.IsTrue(elapsed >= spin - TimeSpan.FromMilliseconds(20), "the call held its thread for the whole spin, took " + elapsed);
        Assert.IsFalse(context.IsHandled, "no timeout failure: failMode closed (503) never fired");
        Assert.AreNotEqual(503, context.HandledStatusCode);
        Assert.IsTrue(nextCalled, "the plugin finished its work and the request went on as if nothing had overrun");
    }

    [TestMethod]
    public async Task AwaitingPlugin_IsStoppedByTheTimeout_AndFailsPerFailMode()
    {
        var timeout = TimeSpan.FromMilliseconds(100);

        var (closed, closedNext, closedElapsed) = await RunAsync(new AwaitForever(), failOpen: false, timeout);
        Assert.IsTrue(closed.IsHandled);
        Assert.AreEqual(503, closed.HandledStatusCode);
        Assert.IsFalse(closedNext);
        Assert.IsTrue(closedElapsed < TimeSpan.FromSeconds(5), "the wall returned near the timeout, took " + closedElapsed);

        var (open, openNext, _) = await RunAsync(new AwaitForever(), failOpen: true, timeout);
        Assert.IsFalse(open.IsHandled);
        Assert.IsTrue(openNext, "failMode open continues the request");
    }
}