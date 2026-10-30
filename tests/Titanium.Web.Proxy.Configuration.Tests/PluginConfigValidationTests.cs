using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>The <c>plugins</c> block and <c>routes[].middleware</c> fail at load, before any plugin runs.</summary>
[TestClass]
public class PluginConfigValidationTests
{
    private static readonly string[] GateMiddleware = ["gate"];
    private static readonly string[] CalloutHostsExpected = ["auth.internal", "geo.internal"];

    private static string TestPlugins => Path.Combine(AppContext.BaseDirectory, "Titanium.Web.Proxy.TestPlugins.dll");

    private static string SamplePlugin => Path.Combine(AppContext.BaseDirectory, "Titanium.Web.Proxy.Examples.RoutePlugin.dll");

    private static TwpConfig Config(PluginConfig plugin, params string[] routeMiddleware) => new()
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
        Plugins = [plugin],
    };

    private static string Errors(PluginConfig plugin, params string[] routeMiddleware) =>
        string.Join("\n", TwpConfigValidator.Validate(Config(plugin, routeMiddleware)));

    [TestMethod]
    public void TheSamplePlugin_IsValid_WithoutNamingItsType()
    {
        var plugin = new PluginConfig { Name = "gate", Assembly = SamplePlugin };
        Assert.AreEqual(string.Empty, Errors(plugin, "gate"));
    }

    [TestMethod]
    public void AMissingAssembly_IsRejected()
    {
        var errors = Errors(new PluginConfig { Name = "gate", Assembly = Path.Combine(AppContext.BaseDirectory, "nope.dll") });
        StringAssert.Contains(errors, "Plugin 'gate'");
        StringAssert.Contains(errors, "does not exist");
    }

    [TestMethod]
    public void AFileThatIsNotAManagedAssembly_IsRejected()
    {
        var file = Path.Combine(Path.GetTempPath(), "twp-not-an-assembly-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllText(file, "not an assembly");
        try
        {
            StringAssert.Contains(Errors(new PluginConfig { Name = "gate", Assembly = file }), "cannot be loaded");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public void ATypeThatDoesNotImplementTheContract_IsRejected()
    {
        var errors = Errors(new PluginConfig
        {
            Name = "gate",
            Assembly = TestPlugins,
            TypeName = "Titanium.Web.Proxy.TestPlugins.NotAPlugin",
        });
        StringAssert.Contains(errors, "does not implement IProxyMiddleware");
    }

    [TestMethod]
    public void AnUnknownType_AndAnAmbiguousAssembly_AreRejected()
    {
        StringAssert.Contains(
            Errors(new PluginConfig { Name = "gate", Assembly = TestPlugins, TypeName = "Nope.Missing" }),
            "has no type 'Nope.Missing'");
        StringAssert.Contains(
            Errors(new PluginConfig { Name = "gate", Assembly = TestPlugins }),
            "several plugin classes");
    }

    [TestMethod]
    public void AContractVersionTheHostDoesNotSupport_IsRejected()
    {
        var errors = Errors(new PluginConfig
        {
            Name = "gate",
            Assembly = TestPlugins,
            TypeName = "Titanium.Web.Proxy.TestPlugins.FutureContractPlugin",
        });
        StringAssert.Contains(errors, "contract version 99");
    }

    [TestMethod]
    public void ATypeWithoutAUsableConstructor_IsRejected()
    {
        var errors = Errors(new PluginConfig
        {
            Name = "gate",
            Assembly = TestPlugins,
            TypeName = "Titanium.Web.Proxy.TestPlugins.NoConstructorPlugin",
        });
        StringAssert.Contains(errors, "public constructor");
    }

    [TestMethod]
    public void NamesFailModeTimeoutAndRouteReferences_AreChecked()
    {
        var good = new PluginConfig
        {
            Name = "gate",
            Assembly = SamplePlugin,
            FailMode = "open",
            TimeoutMs = 250,
            Settings = new Dictionary<string, string> { ["requiredHeader"] = "X-Key" },
        };
        Assert.AreEqual(string.Empty, Errors(good, "gate"));

        StringAssert.Contains(Errors(new PluginConfig { Name = "bad name!", Assembly = SamplePlugin }), "name must be");
        StringAssert.Contains(Errors(new PluginConfig { Name = "gate", Assembly = SamplePlugin, FailMode = "maybe" }), "failMode");
        StringAssert.Contains(Errors(new PluginConfig { Name = "gate", Assembly = SamplePlugin, TimeoutMs = 0 }), "timeoutMs");
        StringAssert.Contains(Errors(new PluginConfig { Name = "gate", Assembly = SamplePlugin, TimeoutMs = 60_001 }), "timeoutMs");
        StringAssert.Contains(Errors(new PluginConfig { Assembly = SamplePlugin }), "missing name");
        StringAssert.Contains(Errors(new PluginConfig { Name = "gate" }), "missing assembly");
        StringAssert.Contains(Errors(good, "other"), "unknown plugin 'other'");
        StringAssert.Contains(Errors(good, "gate", "gate"), "more than once");

        var twice = Config(good, "gate");
        twice.Plugins!.Add(good);
        StringAssert.Contains(string.Join("\n", TwpConfigValidator.Validate(twice)), "Duplicate plugin name 'gate'");
    }

    [TestMethod]
    public void APluginNoRouteNames_IsAWarning_NotAnError()
    {
        var config = Config(new PluginConfig { Name = "gate", Assembly = SamplePlugin });
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
        Assert.IsTrue(TwpConfigValidator.CollectWarnings(config).Any(w => w.Contains("'gate'", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void YamlAndJson_BindThePluginsBlockAndTheRouteList()
    {
        var yaml = $$"""
            plugins:
              - name: gate
                assembly: {{SamplePlugin.Replace("\\", "/", StringComparison.Ordinal)}}
                failMode: open
                timeoutMs: 250
                settings:
                  requiredHeader: X-Key
            clusters:
              - id: c
                destinations:
                  - id: d
                    address: 127.0.0.1
                    port: 9
            routes:
              - id: r
                clusterId: c
                match: { path: /, pathKind: Prefix }
                middleware: [gate]
            """;
        var fromYaml = TwpConfigLoader.LoadYaml(yaml);
        Assert.AreEqual("gate", fromYaml.Plugins!.Single().Name);
        Assert.AreEqual("open", fromYaml.Plugins!.Single().FailMode);
        Assert.AreEqual(250, fromYaml.Plugins!.Single().TimeoutMs);
        Assert.AreEqual("X-Key", fromYaml.Plugins!.Single().Settings!["requiredHeader"]);
        CollectionAssert.AreEqual(GateMiddleware, fromYaml.Routes.Single().Middleware!.ToArray());
        Assert.AreEqual(0, TwpConfigValidator.Validate(fromYaml).Count);

        var fromJson = TwpConfigLoader.LoadJson(TwpConfigLoader.ToJson(fromYaml));
        Assert.AreEqual("gate", fromJson.Plugins!.Single().Name);
        CollectionAssert.AreEqual(GateMiddleware, fromJson.Routes.Single().Middleware!.ToArray());
        Assert.AreEqual(0, UnknownConfigKeyScanner.Scan(yaml, yaml: true).Count);
    }

    [TestMethod]
    public void TheSchema_DescribesThePluginsBlock()
    {
        var schema = TwpConfigSchema.Generate();
        StringAssert.Contains(schema, "\"plugins\"");
        StringAssert.Contains(schema, "\"failMode\"");
        StringAssert.Contains(schema, "\"middleware\"");
    }

    [TestMethod]
    public void ADotNetPlugin_ValidatesWithoutAKind_AndWithKindDotNet()
    {
        Assert.AreEqual(string.Empty, Errors(new PluginConfig { Name = "gate", Assembly = SamplePlugin }, "gate"));
        Assert.AreEqual(string.Empty, Errors(new PluginConfig { Name = "gate", Kind = "dotnet", Assembly = SamplePlugin }, "gate"));
        Assert.AreEqual(string.Empty, Errors(new PluginConfig { Name = "gate", Kind = "", Assembly = SamplePlugin }, "gate"));
        Assert.AreEqual(string.Empty, Errors(new PluginConfig { Name = "gate", Kind = "DotNet", Assembly = SamplePlugin }, "gate"));
    }

    [TestMethod]
    public void AnUnknownKind_IsRejected()
    {
        var errors = Errors(new PluginConfig { Name = "gate", Kind = "python", Assembly = SamplePlugin });
        StringAssert.Contains(errors, "Plugin 'gate'");
        StringAssert.Contains(errors, "kind 'python'");
        StringAssert.Contains(errors, "'dotnet', 'lua', or 'wasm'");
    }

    [TestMethod]
    public void AScriptPlugin_NeedsAnExistingFile_AndNoAssembly()
    {
        var file = Path.Combine(Path.GetTempPath(), "twp-script-" + Guid.NewGuid().ToString("N") + ".lua");
        File.WriteAllText(file, "-- no-op");
        try
        {
            var good = new PluginConfig
            {
                Name = "filter",
                Kind = "lua",
                File = file,
                VmId = "shared-1",
                BodyMode = "buffered",
                CalloutHosts = ["auth.internal"],
                Limits = new PluginLimitsConfig { MemoryMb = 16, MaxInstructions = 1_000_000, PoolSize = 4, EpochDeadlineMs = 50 },
            };
            Assert.AreEqual(string.Empty, Errors(good, "filter"), "validation does not need a registered engine");
            Assert.AreEqual(string.Empty, Errors(new PluginConfig { Name = "filter", Kind = "wasm", File = file }, "filter"));

            StringAssert.Contains(Errors(new PluginConfig { Name = "filter", Kind = "lua" }), "missing file");
            StringAssert.Contains(
                Errors(new PluginConfig { Name = "filter", Kind = "lua", File = file + ".nope" }), "does not exist");
            StringAssert.Contains(
                Errors(new PluginConfig { Name = "filter", Kind = "lua", File = file, Assembly = SamplePlugin }),
                "assembly and typeName only apply to kind 'dotnet'");
            StringAssert.Contains(
                Errors(new PluginConfig { Name = "filter", Kind = "lua", File = file, VmId = "bad id!" }), "vmId");
            StringAssert.Contains(
                Errors(new PluginConfig { Name = "filter", Kind = "lua", File = file, CalloutHosts = [" "] }), "calloutHosts");
            StringAssert.Contains(
                Errors(new PluginConfig { Name = "filter", Kind = "lua", File = file, Limits = new PluginLimitsConfig { Fuel = 0 } }),
                "limits.fuel must be 1 or more");
            StringAssert.Contains(
                Errors(new PluginConfig { Name = "filter", Kind = "lua", File = file, FailMode = "maybe" }), "failMode");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public void ScriptOnlyFields_OnADotNetPlugin_AreRejected()
    {
        var errors = Errors(new PluginConfig
        {
            Name = "gate",
            Assembly = SamplePlugin,
            File = "x.lua",
            Limits = new PluginLimitsConfig { PoolSize = 2 },
        });
        StringAssert.Contains(errors, "file, limits only apply to kind 'lua' or 'wasm'");
    }

    [TestMethod]
    public void YamlAndJson_BindKindFileAndLimits_AndTheSchemaDescribesThem()
    {
        var yaml = """
            plugins:
              - name: filter
                kind: lua
                file: filter.lua
                vmId: edge
                bodyMode: buffered
                calloutHosts: [auth.internal, geo.internal]
                limits:
                  memoryMb: 16
                  maxInstructions: 5000000
                  fuel: 100000
                  poolSize: 4
                  calloutConcurrency: 8
                  bufferedBodyBytes: 65536
                  sharedDataBytes: 4096
                  queueBytes: 8192
                  epochDeadlineMs: 20
            clusters:
              - id: c
                destinations:
                  - id: d
                    address: 127.0.0.1
                    port: 9
            routes:
              - id: r
                clusterId: c
                match: { path: /, pathKind: Prefix }
                middleware: [filter]
            """;
        var plugin = TwpConfigLoader.LoadYaml(yaml).Plugins!.Single();
        Assert.AreEqual("lua", plugin.Kind);
        Assert.AreEqual("filter.lua", plugin.File);
        Assert.AreEqual("edge", plugin.VmId);
        Assert.AreEqual("buffered", plugin.BodyMode);
        CollectionAssert.AreEqual(CalloutHostsExpected, plugin.CalloutHosts!.ToArray());
        Assert.AreEqual(16, plugin.Limits!.MemoryMb);
        Assert.AreEqual(5_000_000L, plugin.Limits.MaxInstructions);
        Assert.AreEqual(100_000L, plugin.Limits.Fuel);
        Assert.AreEqual(4, plugin.Limits.PoolSize);
        Assert.AreEqual(8, plugin.Limits.CalloutConcurrency);
        Assert.AreEqual(65_536L, plugin.Limits.BufferedBodyBytes);
        Assert.AreEqual(4096L, plugin.Limits.SharedDataBytes);
        Assert.AreEqual(8192L, plugin.Limits.QueueBytes);
        Assert.AreEqual(20, plugin.Limits.EpochDeadlineMs);
        Assert.AreEqual(0, UnknownConfigKeyScanner.Scan(yaml, yaml: true).Count);

        var again = TwpConfigLoader.LoadJson(TwpConfigLoader.ToJson(TwpConfigLoader.LoadYaml(yaml))).Plugins!.Single();
        Assert.AreEqual("lua", again.Kind);
        Assert.AreEqual(20, again.Limits!.EpochDeadlineMs);

        var schema = TwpConfigSchema.Generate();
        StringAssert.Contains(schema, "\"kind\"");
        StringAssert.Contains(schema, "\"calloutHosts\"");
        StringAssert.Contains(schema, "\"epochDeadlineMs\"");
        StringAssert.Contains(schema, "\"lua\"");
    }

}
