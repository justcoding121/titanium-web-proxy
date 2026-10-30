using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Caching;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Plugins;
using Titanium.Web.Proxy.Scripting;

namespace Titanium.Cli.Tests;

/// <summary>
/// <c>scriptPipeline</c> and <c>listeners[].streamFilters</c> reach <c>ReverseProxyOptions.ScriptPipeline</c> and a passthrough
/// listener's <c>StreamFilters</c>, and the plugins stay available to routes through <c>RoutePlugins</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ScriptWiringTests
{
    private static readonly string ScriptFile = Path.Combine(Path.GetTempPath(), "twp-cli-wiring-" + Guid.NewGuid().ToString("N") + ".lua");

    [TestInitialize]
    public void Init()
    {
        File.WriteAllText(ScriptFile, "function on_request_headers(ctx) end");
        PluginEngines.Register(new FakeEngine());
    }

    [TestCleanup]
    public void Cleanup()
    {
        PluginEngines.Unregister(PluginKinds.Lua);
        File.Delete(ScriptFile);
    }

    private static PluginHost Load(params string[] names) =>
        PluginHost.Load(names.Select(name => new PluginConfig { Name = name, Kind = "lua", File = ScriptFile }));

    private static ClusterManager Clusters() => new();

    [TestMethod]
    public void NoPipelineNames_BuildNoPipeline()
    {
        using var plugins = Load("a");
        Assert.IsNull(RunCommand.BuildScriptPipeline(plugins, null));
        Assert.IsNull(RunCommand.BuildScriptPipeline(plugins, []));
    }

    [TestMethod]
    public void OnePipelineName_IsThePluginItself_AndSeveralAreAChainInOrder()
    {
        using var plugins = Load("a", "b");

        var single = RunCommand.BuildScriptPipeline(plugins, ["b"]);
        Assert.IsNotNull(single);
        Assert.IsNotInstanceOfType<ScriptPhaseHostChain>(single);
        Assert.AreEqual(ScriptPhases.RequestHeaders, single.Phases);

        var chain = RunCommand.BuildScriptPipeline(plugins, ["b", "a"]);
        Assert.IsInstanceOfType<ScriptPhaseHostChain>(chain);
        Assert.AreEqual(2, ((ScriptPhaseHostChain)chain).Hosts.Count);
    }

    [TestMethod]
    public void AnUnknownPipelineName_Throws()
    {
        using var plugins = Load("a");
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => RunCommand.BuildScriptPipeline(plugins, ["ghost"]));
        StringAssert.Contains(ex.Message, "'ghost'");
    }

    [TestMethod]
    public void ReverseProxyOptions_CarryThePipeline_AndKeepTheRoutePlugins()
    {
        using var plugins = Load("a");
        var options = RunCommand.BuildReverseProxyOptions(
            [], Clusters(), new LoadBalancer(), [], null, null, [], plugins.Plugins, RunCommand.BuildScriptPipeline(plugins, ["a"]));

        Assert.IsNotNull(options.ScriptPipeline);
        Assert.IsTrue(options.RoutePlugins!.ContainsKey("a"), "a route can still list the plugin in middleware");

        var without = RunCommand.BuildReverseProxyOptions([], Clusters(), new LoadBalancer(), [], null, null, [], plugins.Plugins);
        Assert.IsNull(without.ScriptPipeline);
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_True_WhenAScriptPipelineIsSet()
    {
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(new TwpConfig { ScriptPipeline = ["a"] }));
        Assert.IsFalse(RunCommand.ConfigNeedsSessionPath(new TwpConfig { ScriptPipeline = [] }));
    }

    [TestMethod]
    public void ValidateScriptWiring_NamesThePluginThatCannotRunThere()
    {
        using var plugins = Load("a");
        var listener = new ListenerConfig { Port = 8443, Passthrough = true, StreamFilters = ["ghost"] };
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => RunCommand.ValidateScriptWiring(new TwpConfig { Listeners = [listener] }, plugins));
        StringAssert.Contains(ex.Message, "port 8443");
        StringAssert.Contains(ex.Message, "'ghost'");

        RunCommand.ValidateScriptWiring(
            new TwpConfig { ScriptPipeline = ["a"], Listeners = [new ListenerConfig { Port = 1, StreamFilters = ["a"] }] }, plugins);
    }

    [TestMethod]
    public async Task StreamFilters_AreSetOnAPassthroughEndpoint_AndRunThePlugin()
    {
        var plugin = new FakeMiddleware();
        FakeEngine.Next = plugin;
        using var plugins = Load("tcp");
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false);
        var listener = new ListenerConfig { Port = 8443, Passthrough = true, StreamFilters = ["tcp"] };

        RunCommand.ApplyStreamFilters(endPoint, listener, () => plugins);

        Assert.IsNotNull(endPoint.StreamFilters);
        var filter = endPoint.StreamFilters.Single();
        var result = await filter.OnNewConnectionAsync(new StreamConnectionContext { Items = new Dictionary<string, object?>() }, default);
        Assert.AreEqual(ScriptAction.Continue, result.Action);
        Assert.AreEqual(1, plugin.Connections);
    }

    [TestMethod]
    public void StreamFilters_AreLeftAloneWithoutNames_AndRefusedOnAnExplicitListener()
    {
        var transparent = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false);
        RunCommand.ApplyStreamFilters(transparent, new ListenerConfig { Port = 1 }, () => null);
        Assert.IsNull(transparent.StreamFilters);

        var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            RunCommand.ApplyStreamFilters(explicitEndPoint, new ListenerConfig { Port = 1, StreamFilters = ["a"] }, () => null));
    }

    [TestMethod]
    public async Task Reload_SwapsThePipelineWithTheRoutes_AndAFailedCheckKeepsTheOldOne()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-cli-wiring-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var proxy = new ProxyServer(userTrustRootCertificate: false);
            var clusterManager = new ClusterManager();
            var routes = new List<RouteConfig>();
            var pipeline = new List<string> { "a" };
            var plugins = Load("a");
            var refreshed = new List<string[]>();
            Task Reload() => RunCommand.ReloadConfigAsync(
                path, proxy, clusterManager, routes, [], new LoadBalancer(), new MemoryHttpResponseCache(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), static () => null, static _ => { },
                () => refreshed.Add(pipeline.ToArray()), getPlugins: () => plugins, setPlugins: next => plugins = next,
                scriptPipeline: pipeline);

            File.WriteAllText(path, ConfigJson(pipelineNames: "[\"b\", \"a\"]"));
            await Reload();
            CollectionAssert.AreEqual(new[] { "b", "a" }, pipeline);
            CollectionAssert.AreEqual(new[] { "b", "a" }, refreshed.Single());
            Assert.IsNotNull(RunCommand.BuildScriptPipeline(plugins, pipeline));

            // The fake engine now builds a plugin that runs no HTTP phases, so the pipeline cannot be built.
            FakeEngine.PhaseLess = true;
            var before = plugins;
            File.WriteAllText(path, ConfigJson(pipelineNames: "[\"a\"]"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(Reload);
            CollectionAssert.AreEqual(new[] { "b", "a" }, pipeline, "a failed reload keeps the pipeline in force");
            Assert.AreSame(before, plugins, "and the plugins");
            Assert.AreEqual(1, refreshed.Count);
            plugins.Dispose();
        }
        finally
        {
            FakeEngine.PhaseLess = false;
            File.Delete(path);
        }
    }

    private static string ConfigJson(string pipelineNames) => $$"""
        {
          "schemaVersion": "7.0",
          "listeners": [],
          "clusters": [ { "id": "c", "destinations": [ { "id": "d", "address": "127.0.0.1", "port": 9 } ] } ],
          "routes": [ { "id": "r", "clusterId": "c", "match": { "path": "/", "pathKind": "Prefix" } } ],
          "plugins": [
            { "name": "a", "kind": "lua", "file": {{System.Text.Json.JsonSerializer.Serialize(ScriptFile)}} },
            { "name": "b", "kind": "lua", "file": {{System.Text.Json.JsonSerializer.Serialize(ScriptFile)}} }
          ],
          "scriptPipeline": {{pipelineNames}}
        }
        """;

    private sealed class FakeEngine : IPluginEngine
    {
        public static FakeMiddleware? Next { get; set; }

        public static bool PhaseLess { get; set; }

        public string Kind => PluginKinds.Lua;

        public bool TryCreate(PluginConfig config, ILogger? logger, out IProxyMiddleware? middleware, out string? error)
        {
            middleware = PhaseLess ? new PlainMiddleware() : Next ?? new FakeMiddleware();
            Next = null;
            error = null;
            return true;
        }
    }

    private sealed class PlainMiddleware : IProxyMiddleware
    {
        public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken) =>
            next(context, cancellationToken);
    }

    private sealed class FakeMiddleware : IProxyMiddleware, IScriptPhaseHost, IStreamScriptFilter
    {
        public int Connections;

        public ScriptPhases Phases => ScriptPhases.RequestHeaders;

        public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken) =>
            next(context, cancellationToken);

        public ValueTask<StreamFilterResult> OnNewConnectionAsync(StreamConnectionContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Connections);
            return new(StreamFilterResult.Continue);
        }
    }
}
