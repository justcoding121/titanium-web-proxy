using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>
/// F01 leftover regression guard: every sample config under Compat/ (and later samples) must deserialize to the
/// same listener / route / cluster shape stored in Compat/Snapshots/. A changed default or a non-additive schema
/// change fails this test.
/// </summary>
[TestClass]
public class GoldenConfigSnapshotTests
{
    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static string CompatDirectory => Path.Combine(AppContext.BaseDirectory, "Compat");
    private static string SnapshotDirectory => Path.Combine(CompatDirectory, "Snapshots");

    private static IEnumerable<string> SampleConfigFiles() =>
        Directory.GetFiles(CompatDirectory)
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);

    [TestMethod]
    public void EverySampleConfig_MatchesStoredSnapshot()
    {
        Assert.IsTrue(Directory.Exists(SnapshotDirectory), $"missing snapshot directory {SnapshotDirectory}");
        var samples = SampleConfigFiles().ToList();
        Assert.IsTrue(samples.Count >= 4, $"expected sample configs, found {samples.Count}");

        var failures = new List<string>();
        foreach (var file in samples)
        {
            var name = Path.GetFileName(file);
            var snapshotPath = Path.Combine(SnapshotDirectory, Path.GetFileNameWithoutExtension(file) + ".snapshot.json");
            if (!File.Exists(snapshotPath))
            {
                failures.Add($"{name}: missing snapshot {Path.GetFileName(snapshotPath)}");
                continue;
            }

            try
            {
                var config = TwpConfigLoader.LoadFile(file);
                var actual = SerializeShape(config);
                var expected = NormalizeJson(File.ReadAllText(snapshotPath));
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    failures.Add($"{name}: snapshot mismatch (listeners/routes/clusters/server defaults changed)");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Regenerates snapshots when invoked with env <c>TWP_UPDATE_GOLDEN_SNAPSHOTS=1</c>.
    /// Used once when adding a sample; never to silence a real regression.
    /// </summary>
    [TestMethod]
    public void UpdateSnapshots_WhenRequested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TWP_UPDATE_GOLDEN_SNAPSHOTS"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("set TWP_UPDATE_GOLDEN_SNAPSHOTS=1 to rewrite Compat/Snapshots/");
            return;
        }

        Directory.CreateDirectory(Path.Combine(FindRepoCompatDirectory(), "Snapshots"));
        foreach (var file in SampleConfigFiles())
        {
            var config = TwpConfigLoader.LoadFile(file);
            var dest = Path.Combine(FindRepoCompatDirectory(), "Snapshots",
                Path.GetFileNameWithoutExtension(file) + ".snapshot.json");
            File.WriteAllText(dest, SerializeShape(config) + Environment.NewLine);
        }
    }

    private static string SerializeShape(TwpConfig config)
    {
        var shape = new
        {
            schemaVersion = config.SchemaVersion,
            listeners = config.Listeners,
            routes = config.Routes,
            clusters = config.Clusters,
            policies = config.Policies,
            server = config.Server,
            logging = config.Logging,
            plus = config.Plus,
            plugins = config.Plugins,
            staticFiles = config.StaticFiles,
        };
        return NormalizeJson(JsonSerializer.Serialize(shape, SnapshotOptions));
    }

    private static string NormalizeJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement, SnapshotOptions);
    }

    private static string FindRepoCompatDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Titanium.Web.Proxy.Configuration.Tests", "Compat");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not locate tests/Titanium.Web.Proxy.Configuration.Tests/Compat");
    }
}
