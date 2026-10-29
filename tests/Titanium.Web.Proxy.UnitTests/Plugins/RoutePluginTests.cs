using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Plugins;
using Titanium.Web.Proxy.UnitTests.Support;
using static Titanium.Web.Proxy.UnitTests.Plugins.PluginTestKit;

namespace Titanium.Web.Proxy.UnitTests.Plugins;

/// <summary>Per-route .NET plugins on a real proxy: routing, isolation, timeouts, reload, and keep-alive.</summary>
[TestClass]
[DoNotParallelize]
public class RoutePluginTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task APlugin_RunsOnlyOnTheRouteThatNamesIt()
    {
        var marker = NewMarker();
        try
        {
            using var host = PluginHost.Load([Plugin("rec", "RecordingPlugin", settings: [("marker", marker)])]);
            using var origin = new EchoOrigin();
            using var proxy = await StartProxyAsync(
                origin.Port, host, Route("plugged", "/plugged", 1, "rec"), Route("open", "/", 2));
            using var http = proxy.Client();

            var open = await (await http.GetAsync("/open/a")).Content.ReadAsStringAsync();
            Assert.IsFalse(open.Contains("X-Plugin-Ran", StringComparison.OrdinalIgnoreCase), "an unnamed route never reaches the plugin");
            Assert.AreEqual(0, ReadMarker(marker).Length);

            var plugged = await http.GetAsync("/plugged/a");
            Assert.AreEqual(HttpStatusCode.OK, plugged.StatusCode);
            StringAssert.Contains(await plugged.Content.ReadAsStringAsync(), "X-Plugin-Ran: rec");
            Assert.AreEqual(1, ReadMarker(marker).Count(l => l == "invoke"));

            Assert.IsFalse((await (await http.GetAsync("/open/b")).Content.ReadAsStringAsync()).Contains("X-Plugin-Ran", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(1, ReadMarker(marker).Count(l => l == "invoke"));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ThePlugins_OfARoute_RunInOrder_AndTheSampleGatesAndStampsRequests()
    {
        using var host = PluginHost.Load(
        [
            new PluginConfig
            {
                Name = "gate",
                Assembly = SamplePlugin,
                Settings = new Dictionary<string, string> { ["requiredHeader"] = "X-Api-Client", ["stampHeader"] = "X-Stamped" },
            },
            Plugin("rec", "RecordingPlugin"),
        ]);
        using var origin = new EchoOrigin();
        using var proxy = await StartProxyAsync(origin.Port, host, Route("api", "/", 1, "gate", "rec"));
        using var http = proxy.Client();

        var denied = await http.GetAsync("/x");
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.AreEqual("missing X-Api-Client", await denied.Content.ReadAsStringAsync());
        Assert.AreEqual(0, origin.Requests, "a refused request never reaches the origin, or the next plugin");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/x");
        request.Headers.TryAddWithoutValidation("X-Api-Client", "abc");
        var allowed = await http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        var echoed = await allowed.Content.ReadAsStringAsync();
        StringAssert.Contains(echoed, "X-Stamped: header-gate");
        StringAssert.Contains(echoed, "X-Plugin-Ran: recording");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task APluginThatAnswers_StagesItsStatusHeadersAndBody()
    {
        using var host = PluginHost.Load(
        [
            Plugin("deny", "DenyPlugin"),
            Plugin("silent", "SilentPlugin"),
        ]);
        using var origin = new EchoOrigin();
        using var proxy = await StartProxyAsync(
            origin.Port, host, Route("deny", "/deny", 1, "deny"), Route("silent", "/silent", 2, "silent"));
        using var http = proxy.Client();

        var teapot = await http.GetAsync("/deny");
        Assert.AreEqual((HttpStatusCode)418, teapot.StatusCode);
        Assert.AreEqual("teapot", await teapot.Content.ReadAsStringAsync());
        Assert.AreEqual("deny-plugin", teapot.Headers.GetValues("X-Denied-By").Single());

        var silent = await http.GetAsync("/silent");
        Assert.AreEqual(HttpStatusCode.Forbidden, silent.StatusCode, "a plugin that neither continues nor answers stops the request");
        Assert.AreEqual(0, origin.Requests);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ARouteNamingAMissingPlugin_IsRefused_NeverOpen()
    {
        using var origin = new EchoOrigin();
        using var withHost = await StartProxyAsync(origin.Port, PluginHost.Load([]), Route("r", "/", 1, "ghost"));
        using var http = withHost.Client();
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/")).StatusCode);

        using var without = await StartProxyAsync(origin.Port, null, Route("r", "/", 1, "ghost"));
        using var other = without.Client();
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await other.GetAsync("/")).StatusCode);
        Assert.AreEqual(0, origin.Requests);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task AThrowingPlugin_IsIsolated_FailClosedAnswers503_FailOpenContinues()
    {
        using var meter = new PluginMeter();
        using var host = PluginHost.Load(
        [
            Plugin("boom-closed", "ThrowingPlugin"),
            Plugin("boom-open", "ThrowingPlugin", failMode: "open"),
            Plugin("boom-after", "ThrowingPlugin", failMode: "closed", settings: [("afterNext", "true")]),
            Plugin("rec", "RecordingPlugin"),
        ]);
        using var origin = new EchoOrigin();
        using var proxy = await StartProxyAsync(
            origin.Port,
            host,
            Route("closed", "/closed", 1, "boom-closed"),
            Route("open", "/open", 2, "boom-open", "rec"),
            Route("after", "/after", 3, "boom-after"));
        using var http = proxy.Client();

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/closed")).StatusCode);
        Assert.AreEqual(0, origin.Requests, "fail-closed never forwards the request");

        var open = await http.GetAsync("/open");
        Assert.AreEqual(HttpStatusCode.OK, open.StatusCode);
        StringAssert.Contains(await open.Content.ReadAsStringAsync(), "X-Plugin-Ran", "fail-open runs the rest of the chain");
        Assert.AreEqual(1, origin.Requests);

        Assert.AreEqual(
            HttpStatusCode.ServiceUnavailable,
            (await http.GetAsync("/after")).StatusCode,
            "a plugin that fails after calling next still fails closed");

        // The proxy keeps serving after the failures.
        Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/open")).StatusCode);

        Assert.AreEqual(1, meter.Total("twp.plugin.errors{plugin=boom-closed,kind=exception}"));
        Assert.AreEqual(2, meter.Total("twp.plugin.errors{plugin=boom-open,kind=exception}"));
        Assert.AreEqual(1, meter.Total("twp.plugin.errors{plugin=boom-after,kind=exception}"));
        Assert.AreEqual(2, meter.Total("twp.plugin.duration{plugin=boom-open}"), "every call is timed, including failures");
    }

    [TestMethod]
    [Timeout(60_000)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ASlowPlugin_TimesOut_WhetherOrNotItWatchesTheToken(bool honorCancellation)
    {
        using var meter = new PluginMeter();
        var slow = ("delayMs", "4000");
        var honor = ("honorCancellation", honorCancellation ? "true" : "false");
        using var host = PluginHost.Load(
        [
            Plugin("slow-closed", "SlowPlugin", timeoutMs: 150, settings: [slow, honor]),
            Plugin("slow-open", "SlowPlugin", failMode: "open", timeoutMs: 150, settings: [slow, honor]),
        ]);
        using var origin = new EchoOrigin();
        using var proxy = await StartProxyAsync(
            origin.Port, host, Route("closed", "/closed", 1, "slow-closed"), Route("open", "/open", 2, "slow-open"));
        using var http = proxy.Client();

        var watch = Stopwatch.StartNew();
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/closed")).StatusCode);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(3), $"answered after {watch.Elapsed}, not after the plugin finished");

        watch.Restart();
        var open = await http.GetAsync("/open");
        Assert.AreEqual(HttpStatusCode.OK, open.StatusCode);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(3), $"answered after {watch.Elapsed}");
        Assert.IsFalse((await open.Content.ReadAsStringAsync()).Contains("X-Late-Plugin", StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(1, meter.Total("twp.plugin.errors{plugin=slow-closed,kind=timeout}"));
        Assert.AreEqual(1, meter.Total("twp.plugin.errors{plugin=slow-open,kind=timeout}"));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task APluginThatFinishesAfterItsTimeout_CannotChangeTheRequest()
    {
        using var host = PluginHost.Load(
            [Plugin("slow", "SlowPlugin", failMode: "open", timeoutMs: 100, settings: [("delayMs", "600")])]);
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var session = TestSessions.Create(proxy);
        var context = new ProxyMiddlewareContext { Session = session };
        var continued = 0;

        await host.Plugins["slow"].InvokeAsync(context, (_, _) =>
        {
            Interlocked.Increment(ref continued);
            return default;
        }, CancellationToken.None);

        Assert.AreEqual(1, continued, "fail-open continues the chain once");
        Assert.IsFalse(context.IsHandled);
        await Task.Delay(900);
        Assert.IsFalse(context.IsHandled, "the late plugin wrote to its own copy of the context");
        Assert.AreEqual(1, continued, "the late plugin's next is ignored");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task KeepAlive_DoesNotLeakPluginState_BetweenRequests()
    {
        using var host = PluginHost.Load([Plugin("rec", "RecordingPlugin")]);
        using var origin = new EchoOrigin();
        using var proxy = await StartProxyAsync(origin.Port, host, Route("r", "/", 1, "rec"));
        using var http = proxy.Client();

        for (var call = 1; call <= 4; call++)
        {
            var echoed = await (await http.GetAsync("/same-connection")).Content.ReadAsStringAsync();
            StringAssert.Contains(echoed, "X-Plugin-Ran: recording");
            StringAssert.Contains(echoed, $"X-Plugin-Calls: {call}");
            Assert.IsFalse(
                echoed.Contains("X-Plugin-Leak", StringComparison.OrdinalIgnoreCase),
                "per-request Items from an earlier request must not be visible");
        }

        Assert.AreEqual(1, origin.Connections, "the requests shared one upstream connection");
        Assert.AreEqual(4, origin.Requests);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ARouteWithoutPlugins_PaysNothing_WhileAPluginRouteExists()
    {
        using var meter = new PluginMeter();
        using var host = PluginHost.Load([Plugin("rec", "RecordingPlugin")]);
        using var origin = new EchoOrigin();
        using var proxy = await StartProxyAsync(
            origin.Port, host, Route("plugged", "/plugged", 1, "rec"), Route("open", "/", 2));
        using var http = proxy.Client();

        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/open")).StatusCode);
        }

        Assert.AreEqual(0, meter.Count("twp.plugin."), "no plugin call was timed or counted");
        await http.GetAsync("/plugged");
        Assert.AreEqual(1, meter.Total("twp.plugin.duration{plugin=rec}"));
    }

    [TestMethod]
    public async Task AnUnloadedHost_FailsEveryCallPerItsFailMode()
    {
        using var meter = new PluginMeter();
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var host = PluginHost.Load(
        [
            Plugin("closed", "RecordingPlugin"),
            Plugin("open", "RecordingPlugin", failMode: "open"),
        ]);
        var closed = host.Plugins["closed"];
        var open = host.Plugins["open"];
        host.Dispose();
        Assert.IsTrue(host.IsDisposed);

        var context = new ProxyMiddlewareContext { Session = TestSessions.Create(proxy) };
        await closed.InvokeAsync(context, (_, _) => default, CancellationToken.None);
        Assert.IsTrue(context.IsHandled);
        Assert.AreEqual(503, context.HandledStatusCode);

        var continued = false;
        var second = new ProxyMiddlewareContext { Session = context.Session };
        await open.InvokeAsync(second, (_, _) =>
        {
            continued = true;
            return default;
        }, CancellationToken.None);
        Assert.IsTrue(continued);
        Assert.IsFalse(second.IsHandled);
        Assert.AreEqual(1, meter.Total("twp.plugin.errors{plugin=closed,kind=unloaded}"));
        Assert.AreEqual(1, meter.Total("twp.plugin.errors{plugin=open,kind=unloaded}"));
    }

    [TestMethod]
    public void Settings_ReachThePluginConstructor_AndABadPluginFailsTheLoadNamingIt()
    {
        var marker = NewMarker();
        try
        {
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => PluginHost.Load(
            [
                Plugin("good", "RecordingPlugin", settings: [("marker", marker)]),
                Plugin("bad", "FaultyConstructorPlugin"),
            ]));
            StringAssert.Contains(ex.Message, "Plugin 'bad'");
            StringAssert.Contains(ex.Message, "constructor failure");
            Assert.AreEqual(
                1,
                ReadMarker(marker).Count(l => l == "disposed"),
                "a plugin loaded before the failure is disposed, so a failed load keeps nothing");

            foreach (var (config, expected) in new[]
            {
                (Plugin("future", "FutureContractPlugin"), "contract version 99"),
                (Plugin("noctor", "NoConstructorPlugin"), "public constructor"),
                (Plugin("nope", "NotAPlugin"), "does not implement"),
                (Plugin("dup", "RecordingPlugin", failMode: "sideways"), "failMode"),
                (Plugin("late", "RecordingPlugin", timeoutMs: 0), "timeoutMs"),
            })
            {
                StringAssert.Contains(
                    Assert.ThrowsExactly<InvalidOperationException>(() => PluginHost.Load([config])).Message, expected);
            }

            StringAssert.Contains(
                Assert.ThrowsExactly<InvalidOperationException>(() => PluginHost.Load(
                    [Plugin("a", "RecordingPlugin"), Plugin("a", "RecordingPlugin")])).Message,
                "defined twice");
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [TestMethod]
    public void Reload_DisposesThePlugins_AndUnloadsTheirContexts()
    {
        var marker = NewMarker();
        try
        {
            var handles = LoadUseAndRetire(marker, out var stillAlive);
            Assert.IsTrue(stillAlive.Length > 0);
            CollectionAssert.AreEqual(new[] { "invoke", "disposed" }, ReadMarker(marker));
            Assert.IsTrue(CollectedWithin(handles), "every plugin load context was reclaimed after the host retired");
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] LoadUseAndRetire(string marker, out WeakReference[] contextsBefore)
    {
        var host = PluginHost.Load(
        [
            Plugin("rec", "RecordingPlugin", settings: [("marker", marker)]),
            Plugin("rec2", "RecordingPlugin"),
        ]);
        contextsBefore = host.LoadContexts.ToArray();
        Assert.IsTrue(contextsBefore.All(h => h.IsAlive));
        Assert.AreNotSame(contextsBefore[0].Target, contextsBefore[1].Target, "one load context per plugin");

        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var context = new ProxyMiddlewareContext { Session = TestSessions.Create(proxy) };
        host.Plugins["rec"].InvokeAsync(context, (_, _) => default, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        var handles = host.LoadContexts.Select(h => new WeakReference(h.Target)).ToArray();
        host.Retire();
        Assert.IsTrue(host.IsDisposed, "nothing was in flight, so retiring disposes at once");
        return handles;
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ARetiredHost_FinishesItsCallsInFlight_ThenDisposes()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var host = PluginHost.Load(
            [Plugin("slow", "SlowPlugin", failMode: "open", timeoutMs: 10_000, settings: [("delayMs", "400")])]);
        var context = new ProxyMiddlewareContext { Session = TestSessions.Create(proxy) };
        var continued = false;

        var call = host.Plugins["slow"].InvokeAsync(context, (_, _) =>
        {
            continued = true;
            return default;
        }, CancellationToken.None).AsTask();
        await Task.Delay(100);
        host.Retire();
        Assert.IsFalse(host.IsDisposed, "a call is still running on the retired host");

        await call;
        Assert.IsTrue(context.IsHandled, "the late plugin's answer is kept: it finished inside its timeout");
        Assert.IsFalse(continued);
        Assert.IsTrue(host.IsDisposed, "the last call out disposed the host");
    }

    [TestMethod]
    public void Dispose_IsIdempotent_AndLeavesNoContexts()
    {
        var host = PluginHost.Load([Plugin("rec", "RecordingPlugin")]);
        host.Dispose();
        host.Dispose();
        host.Retire();
        Assert.IsTrue(host.IsDisposed);
        Assert.AreEqual(0, host.LoadContexts.Count);
    }
}
