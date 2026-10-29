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
        CollectionAssert.AreEqual(new[] { "gate" }, fromYaml.Routes.Single().Middleware!.ToArray());
        Assert.AreEqual(0, TwpConfigValidator.Validate(fromYaml).Count);

        var fromJson = TwpConfigLoader.LoadJson(TwpConfigLoader.ToJson(fromYaml));
        Assert.AreEqual("gate", fromJson.Plugins!.Single().Name);
        CollectionAssert.AreEqual(new[] { "gate" }, fromJson.Routes.Single().Middleware!.ToArray());
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
    public void TheContractVersion_IsWithinTheSupportedRange()
    {
        Assert.IsTrue(ProxyPluginContract.MinimumSupportedVersion <= ProxyPluginContract.CurrentVersion);
        Assert.AreEqual(1, ProxyPluginContract.CurrentVersion);
    }
}
