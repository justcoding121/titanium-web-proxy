using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>
///     Upgrade compatibility: configurations written for older releases (<c>Compat/</c>) must keep loading and
///     validating. The schema bump rules are in <c>Compat/README.md</c>.
/// </summary>
[TestClass]
public class ConfigCompatibilityTests
{
    private static string CompatDirectory => Path.Combine(AppContext.BaseDirectory, "Compat");

    private static IEnumerable<string> GoldenFiles() =>
        Directory.GetFiles(CompatDirectory)
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);

    [TestMethod]
    public void EveryGoldenConfig_LoadsAndValidatesWithoutErrors()
    {
        var files = GoldenFiles().ToList();
        Assert.IsTrue(files.Count >= 4, $"expected the golden corpus, found {files.Count} file(s) in {CompatDirectory}");

        var failures = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var config = TwpConfigLoader.LoadFile(file);
                var errors = TwpConfigValidator.Validate(config);
                if (errors.Count > 0)
                    failures.Add($"{Path.GetFileName(file)}: {string.Join(" | ", errors)}");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: failed to load: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void V70MinimalReverse_KeepsItsClassicForwardHostMeaning()
    {
        var config = TwpConfigLoader.LoadFile(Path.Combine(CompatDirectory, "v7.0-minimal-reverse.yaml"));

        Assert.AreEqual("7.0", config.SchemaVersion);
        var listener = config.Listeners.Single();
        Assert.AreEqual("127.0.0.1", listener.Host);
        Assert.AreEqual(8000, listener.Port);
        Assert.IsFalse(listener.DecryptSsl);
        Assert.AreEqual("127.0.0.1", listener.ForwardHost);
        Assert.AreEqual(8080, listener.ForwardPort);

        // Sections that did not exist in 7.0 stay at their do-nothing defaults.
        Assert.AreEqual(0, config.Routes.Count);
        Assert.AreEqual(0, config.Clusters.Count);
        Assert.AreEqual(0, config.Policies.Count);
        Assert.IsNull(config.Plugins);
        Assert.IsNull(config.Server);
        Assert.IsNull(config.Logging);
        Assert.IsNull(config.Plus);
    }

    [TestMethod]
    public void V70RoutesAndClusters_KeepRoutingMeaning()
    {
        var config = TwpConfigLoader.LoadFile(Path.Combine(CompatDirectory, "v7.0-routes-clusters.json"));

        Assert.AreEqual("7.0", config.SchemaVersion);
        Assert.AreEqual(2, config.Clusters.Count);
        Assert.AreEqual(2, config.Clusters.Single(c => c.Id == "web").Destinations.Count);

        var api = config.Routes.Single(r => r.Id == "api");
        Assert.AreEqual("api", api.ClusterId);
        Assert.AreEqual("/api", api.Match.Path);
        Assert.IsTrue(api.Order < config.Routes.Single(r => r.Id == "default").Order);

        Assert.AreEqual("./public", config.StaticFiles?.Root);
        Assert.IsFalse(config.Plus?.Enabled ?? true);
    }

    [TestMethod]
    public void V71ServerLoggingAndPolicies_KeepTheirKeys()
    {
        var config = TwpConfigLoader.LoadFile(Path.Combine(CompatDirectory, "v7.1-server-logging-policies.yaml"));

        Assert.AreEqual("7.1", config.SchemaVersion);
        Assert.AreEqual("Warning", config.Logging?.MinimumLevel);
        Assert.IsNotNull(config.Server);
        Assert.AreEqual("transparent", config.Listeners.Single().Type);
        Assert.AreEqual(true, config.Listeners.Single().EnableHttp2);
        CollectionAssert.AreEqual(new[] { 502, 503 },
            config.Clusters.Single().HealthCheck!.Passive!.FailureStatusCodes!.ToArray());
    }

    [TestMethod]
    public void NewerMinorVersionWithUnknownKeys_StillLoadsOnThisBuild()
    {
        // Rule 3 of Compat/README.md: unknown keys are ignored, so a file written for a later 7.x loads here.
        var yaml = TwpConfigLoader.LoadYaml("""
            schemaVersion: "7.99"
            someFutureSection:
              enabled: true
            listeners:
              - host: 127.0.0.1
                port: 8000
                forwardHost: 127.0.0.1
                forwardPort: 8080
                someFutureListenerKey: 1
            """);
        Assert.AreEqual("7.99", yaml.SchemaVersion);
        Assert.AreEqual(0, TwpConfigValidator.Validate(yaml).Count);

        var json = TwpConfigLoader.LoadJson("""
            {
              "schemaVersion": "7.99",
              "someFutureSection": { "enabled": true },
              "listeners": [{ "host": "127.0.0.1", "port": 8000, "forwardHost": "127.0.0.1", "forwardPort": 8080, "someFutureListenerKey": 1 }]
            }
            """);
        Assert.AreEqual(1, json.Listeners.Count);
        Assert.AreEqual(0, TwpConfigValidator.Validate(json).Count);
    }
}
