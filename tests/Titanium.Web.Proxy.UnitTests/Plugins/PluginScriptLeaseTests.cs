using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Plugins;

namespace Titanium.Web.Proxy.UnitTests.Plugins;

/// <summary>
/// A script plugin is handed to the listener-wide pipeline and to passthrough listeners as a lease on its
/// <see cref="PluginHost"/>: calls count against the host, and a call after the host is gone is refused, never run.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PluginScriptLeaseTests
{
    [TestCleanup]
    public void Cleanup()
    {
        PluginEngines.Unregister(PluginKinds.Lua);
        PluginEngines.Unregister(PluginKinds.Wasm);
    }

    private static PluginHost Load(string name, bool failOpen = false, Func<ScriptingMiddleware>? make = null)
    {
        var created = make ?? (() => new ScriptingMiddleware());
        PluginEngines.Register(PluginKinds.Lua, () => new Engine(created));
        return PluginHost.Load([
            new PluginConfig { Name = name, Kind = "lua", File = "x.lua", FailMode = failOpen ? "open" : "closed" },
        ]);
    }

    private static ScriptPhaseContext Phase() => new() { Phase = ScriptPhases.RequestHeaders, Items = new Dictionary<string, object?>() };

    private static StreamConnectionContext Connection() => new() { Items = new Dictionary<string, object?>() };

    [TestMethod]
    public async Task GetScriptHost_RunsThePluginsPhasesWhileTheHostIsLoaded()
    {
        var plugin = new ScriptingMiddleware();
        using var host = Load("gate", make: () => plugin);

        var script = host.GetScriptHost("gate");
        Assert.IsNotNull(script);
        Assert.AreEqual(ScriptPhases.RequestHeaders | ScriptPhases.Log, script.Phases);
        Assert.AreEqual(ScriptBodyMode.Buffered, script.BodyMode);

        var result = await script.OnRequestHeadersAsync(Phase(), default);
        Assert.AreEqual(ScriptAction.Deny, result.Action);
        Assert.AreEqual(1, plugin.HeaderCalls);

        await script.OnLogAsync(Phase(), default);
        Assert.AreEqual(1, plugin.LogCalls);
    }

    [TestMethod]
    public void GetScriptHost_IsNullForAnUnknownName()
    {
        using var host = Load("gate");
        Assert.IsNull(host.GetScriptHost("ghost"));
        Assert.IsFalse(host.CanFilterStreams("ghost"));
        Assert.IsTrue(host.CanFilterStreams("gate"));
    }

    [TestMethod]
    public async Task ACallAfterTheHostWasDisposed_IsRefusedWhenClosed_AndPassesWhenOpen()
    {
        var closedPlugin = new ScriptingMiddleware();
        var closed = Load("gate", make: () => closedPlugin);
        var closedScript = closed.GetScriptHost("gate")!;
        closed.Dispose();

        var refused = await closedScript.OnRequestHeadersAsync(Phase(), default);
        Assert.AreEqual(ScriptAction.Deny, refused.Action);
        Assert.AreEqual(503, refused.LocalResponse!.Status);
        Assert.AreEqual(0, closedPlugin.HeaderCalls, "a disposed script is never called");

        var openPlugin = new ScriptingMiddleware();
        var open = Load("gate", failOpen: true, make: () => openPlugin);
        var openScript = open.GetScriptHost("gate")!;
        open.Dispose();

        Assert.AreEqual(ScriptAction.Continue, (await openScript.OnRequestHeadersAsync(Phase(), default)).Action);
        Assert.AreEqual(0, openPlugin.HeaderCalls);
    }

    [TestMethod]
    public async Task ARetiredHost_WaitsForTheScriptCallInFlight_BeforeItDisposesThePlugin()
    {
        var plugin = new ScriptingMiddleware { Gate = new TaskCompletionSource() };
        var host = Load("gate", make: () => plugin);
        var script = host.GetScriptHost("gate")!;

        var call = script.OnRequestHeadersAsync(Phase(), default).AsTask();
        host.Retire();
        Assert.IsFalse(host.IsDisposed, "the call in flight keeps the host alive");

        plugin.Gate.SetResult();
        await call;
        Assert.IsTrue(host.IsDisposed);
        Assert.IsTrue(plugin.Disposed);
    }

    [TestMethod]
    public async Task AReloadableStreamFilter_PinsAConnectionToThePluginItStartedOn()
    {
        var oldPlugin = new ScriptingMiddleware();
        var newPlugin = new ScriptingMiddleware();
        var current = Load("tcp", make: () => oldPlugin);
        var filter = new ReloadableStreamFilter("tcp", () => current);

        var connection = Connection();
        Assert.AreEqual(ScriptAction.Continue, (await filter.OnNewConnectionAsync(connection, default)).Action);

        // A reload swaps in a new plugin set; the connection keeps talking to the plugin it began with.
        var next = Load("tcp", make: () => newPlugin);
        var previous = current;
        current = next;

        Assert.AreEqual(ScriptAction.Continue, (await filter.OnDownstreamDataAsync(connection, "ab"u8.ToArray(), default)).Action);
        Assert.AreEqual(1, oldPlugin.DataCalls);
        Assert.AreEqual(0, newPlugin.DataCalls);

        // Once the old set is retired its scripts are gone. The connection was admitted already, so it keeps flowing,
        // unfiltered, and it never lands on the new plugin that knows nothing about it.
        previous.Retire();
        Assert.IsTrue(previous.IsDisposed);
        Assert.AreEqual(ScriptAction.Continue, (await filter.OnDownstreamDataAsync(connection, "cd"u8.ToArray(), default)).Action);
        Assert.AreEqual(1, oldPlugin.DataCalls);
        Assert.AreEqual(0, newPlugin.DataCalls);
        await filter.OnCloseAsync(connection);
        Assert.AreEqual(0, connection.Items.Count, "the pin is dropped when the connection closes");

        var fresh = Connection();
        await filter.OnNewConnectionAsync(fresh, default);
        Assert.AreEqual(1, newPlugin.ConnectCalls, "a new connection goes to the plugin set in force");
        next.Dispose();
    }

    [TestMethod]
    public async Task AReloadableStreamFilter_RefusesAConnectionWhenItsPluginIsNoLongerLoaded()
    {
        using var host = Load("other");
        var filter = new ReloadableStreamFilter("tcp", () => host);

        var result = await filter.OnNewConnectionAsync(Connection(), default);
        Assert.AreEqual(ScriptAction.Deny, result.Action, "a connection must not skip the filter the operator put on the listener");
    }

    private sealed class Engine(Func<ScriptingMiddleware> create) : IPluginEngine
    {
        public string Kind => PluginKinds.Lua;

        public bool TryCreate(PluginConfig config, ILogger? logger, out IProxyMiddleware? middleware, out string? error)
        {
            middleware = create();
            error = null;
            return true;
        }
    }

    private sealed class ScriptingMiddleware : IProxyMiddleware, IScriptPhaseHost, IStreamScriptFilter, IDisposable
    {
        public int HeaderCalls;
        public int LogCalls;
        public int ConnectCalls;
        public int DataCalls;
        public int CloseCalls;
        public bool Disposed;
        public TaskCompletionSource? Gate { get; init; }

        public ScriptPhases Phases => ScriptPhases.RequestHeaders | ScriptPhases.Log;

        public ScriptBodyMode BodyMode => ScriptBodyMode.Buffered;

        public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken) =>
            next(context, cancellationToken);

        public async ValueTask<ScriptPhaseResult> OnRequestHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref HeaderCalls);
            if (Gate is not null)
            {
                await Gate.Task;
            }

            return ScriptPhaseResult.Deny(418, "teapot");
        }

        public ValueTask OnLogAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref LogCalls);
            return default;
        }

        public ValueTask<StreamFilterResult> OnNewConnectionAsync(StreamConnectionContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ConnectCalls);
            return new(StreamFilterResult.Continue);
        }

        public ValueTask<StreamFilterResult> OnDownstreamDataAsync(
            StreamConnectionContext context, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DataCalls);
            return new(StreamFilterResult.Continue);
        }

        public ValueTask OnCloseAsync(StreamConnectionContext context)
        {
            Interlocked.Increment(ref CloseCalls);
            return default;
        }

        public void Dispose() => Disposed = true;
    }
}
