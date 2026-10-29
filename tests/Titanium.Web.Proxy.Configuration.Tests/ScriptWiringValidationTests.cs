using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary><c>scriptPipeline</c> and <c>listeners[].streamFilters</c> name script plugins, and fail at load when they cannot run.</summary>
[TestClass]
public class ScriptWiringValidationTests
{
    private static readonly string ScriptFile = WriteScript();

    private static string WriteScript()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-wiring-" + Guid.NewGuid().ToString("N") + ".lua");
        File.WriteAllText(path, "function on_request_headers(ctx) end");
        return path;
    }

    private static PluginConfig Lua(string name) => new() { Name = name, Kind = PluginKinds.Lua, File = ScriptFile };

    private static TwpConfig Config(IList<PluginConfig> plugins, IList<string>? pipeline = null,
        ListenerConfig? listener = null, params string[] routeMiddleware) => new()
    {
        Clusters =
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 9 }],
            },
        ],
        Routes =
        [
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Middleware = routeMiddleware.Length == 0 ? null : routeMiddleware,
            },
        ],
        Plugins = plugins,
        ScriptPipeline = pipeline,
        Listeners = listener is null ? [] : [listener],
    };

    private static string Errors(TwpConfig config) => string.Join("\n", TwpConfigValidator.Validate(config));

    [TestMethod]
    public void AScriptPipelineOfLoadedScripts_IsValid()
    {
        Assert.AreEqual(string.Empty, Errors(Config([Lua("a"), Lua("b")], ["a", "b"])));
    }

    [TestMethod]
    public void AnUnknownPipelineEntry_IsRejected()
    {
        StringAssert.Contains(Errors(Config([Lua("a")], ["ghost"])), "scriptPipeline references unknown plugin 'ghost'");
    }

    [TestMethod]
    public void ADuplicateOrBlankPipelineEntry_IsRejected()
    {
        var errors = Errors(Config([Lua("a")], ["a", "a", " "]));
        StringAssert.Contains(errors, "scriptPipeline lists plugin 'a' more than once");
        StringAssert.Contains(errors, "scriptPipeline has an entry without a name");
    }

    [TestMethod]
    public void ADotNetPluginInThePipeline_IsRejected()
    {
        var dotnet = new PluginConfig
        {
            Name = "net",
            Assembly = Path.Combine(AppContext.BaseDirectory, "Titanium.Web.Proxy.Examples.RoutePlugin.dll"),
        };
        StringAssert.Contains(Errors(Config([dotnet], ["net"])), "only 'lua' and 'wasm' plugins run there");
    }

    [TestMethod]
    public void StreamFilters_NeedAPassthroughListener()
    {
        var plain = new ListenerConfig { Port = 8080, StreamFilters = ["a"] };
        StringAssert.Contains(Errors(Config([Lua("a")], listener: plain)), "streamFilters needs passthrough: true and decryptSsl: false");

        var passthrough = new ListenerConfig { Port = 8443, Passthrough = true, ForwardHost = "127.0.0.1", ForwardPort = 9, StreamFilters = ["a"] };
        Assert.AreEqual(string.Empty, Errors(Config([Lua("a")], listener: passthrough)));
    }

    [TestMethod]
    public void AnUnknownStreamFilter_IsRejected()
    {
        var listener = new ListenerConfig { Port = 8443, Passthrough = true, ForwardHost = "127.0.0.1", ForwardPort = 9, StreamFilters = ["ghost"] };
        StringAssert.Contains(Errors(Config([Lua("a")], listener: listener)), "Listener port 8443: streamFilters references unknown plugin 'ghost'");
    }

    [TestMethod]
    public void APluginUsedOnlyByThePipelineOrAListener_IsNotReportedUnused()
    {
        var listener = new ListenerConfig { Port = 8443, Passthrough = true, ForwardHost = "127.0.0.1", ForwardPort = 9, StreamFilters = ["b"] };
        var warnings = TwpConfigValidator.CollectWarnings(Config([Lua("a"), Lua("b"), Lua("c")], ["a"], listener));

        Assert.IsFalse(warnings.Any(w => w.Contains("Plugin 'a'", StringComparison.Ordinal)));
        Assert.IsFalse(warnings.Any(w => w.Contains("Plugin 'b'", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(w => w.Contains("Plugin 'c' is loaded but", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void APluginInThePipelineAndInARouteMiddleware_Warns()
    {
        var warnings = TwpConfigValidator.CollectWarnings(Config([Lua("a")], ["a"], routeMiddleware: ["a"]));
        Assert.IsTrue(warnings.Any(w => w.Contains("Plugin 'a' is in scriptPipeline and in a route's middleware", StringComparison.Ordinal)));
    }
}
